using System.IO;
using System.Windows;
using System.Windows.Media;
using Vacate.Abstractions.Execution;
using Vacate.Abstractions.Model;
using Vacate.App.Localization;
using Vacate.Core.Execution;
using Vacate.Core.Journal;
using Vacate.Core.Safety;
using Vacate.Platform.Windows.Files;
using Vacate.Platform.Windows.Registry;

namespace Vacate.App.Views;

/// <summary>
/// Удаление программы: подтверждение, штатный деинсталлятор, зачистка следов.
/// </summary>
/// <remarks>
/// Порядок шагов не произвольный. Пока программа установлена, её каталог — не остаток,
/// а рабочие файлы: удалив их первыми, мы оставили бы систему с записью в реестре,
/// ведущей на исчезнувший деинсталлятор, то есть с программой, которую больше нечем удалить.
///
/// Ни один шаг не выполняется сам по себе. Между поиском следов и их удалением стоит
/// список с отметками, и по умолчанию отмечено только то, в чём поиск уверен.
/// </remarks>
public partial class UninstallWindow : Window
{
    private readonly InstalledApp _app;
    private Step _step = Step.Confirm;
    private List<LeftoverRow> _leftovers = [];

    /// <summary>Удаляем без деинсталлятора: его нет, и запись придётся убрать самим.</summary>
    private bool _forced;

    public UninstallWindow(InstalledApp app)
    {
        ArgumentNullException.ThrowIfNull(app);

        _app = app;

        InitializeComponent();
        ShowConfirmStep();
    }

    private enum Step
    {
        Confirm,
        Running,
        Leftovers,
        Done,
    }

    private void ShowConfirmStep()
    {
        StepTitle.Text = Strings.Get("Uninstall.WindowTitle");
        StepSubtitle.Text = Strings.Get("Uninstall.ConfirmSubtitle");

        AppName.Text = _app.DisplayName;

        var details = new List<string>
        {
            $"{Strings.Get("Uninstall.Publisher"),-11}{_app.Publisher ?? Strings.Get("Uninstall.NotSpecified")}",
            $"{Strings.Get("Uninstall.Version"),-11}{_app.Version ?? Strings.Get("Uninstall.VersionUnknown")}",
            $"{Strings.Get("Uninstall.Location"),-11}{_app.InstallLocation ?? Strings.Get("Uninstall.NotSpecified")}",
            $"{Strings.Get("Uninstall.Scope"),-11}{Strings.Get(_app.Scope == InstallScope.User ? "Uninstall.ForYou" : "Uninstall.ForAll")}",
        };

        AppDetails.Text = string.Join(Environment.NewLine, details);

        var warnings = new List<string>();

        if (_app.LooksLikeRuntime)
        {
            // Не запрет, а честное предупреждение: удалять компоненты иногда нужно,
            // но человек должен знать, что ломает не одну программу.
            warnings.Add(Strings.Get("Uninstall.WarnRuntime"));
        }

        if (ForcedUninstall.IsApplicable(_app))
        {
            // Случай распространённый: программу снесли вручную, а запись осталась.
            // Штатно удалить нечем, и человек видит её в списке навсегда.
            warnings.Add(Strings.Get(_app.CanUninstall
                ? "Uninstall.WarnMissingUninstaller"
                : "Uninstall.WarnNoUninstaller"));
        }

        if (_app.Scope == InstallScope.Machine)
        {
            warnings.Add(Strings.Get("Uninstall.WarnMachineScope"));
        }

        if (warnings.Count > 0)
        {
            WarningCard.Visibility = Visibility.Visible;
            WarningText.Text = string.Join(Environment.NewLine + Environment.NewLine, warnings);
        }

        ActionButton.Content = Strings.Get(
            ForcedUninstall.IsApplicable(_app) ? "Uninstall.StartForced" : "Uninstall.Start");
    }

    private async void OnAction(object sender, RoutedEventArgs e)
    {
        switch (_step)
        {
            case Step.Confirm:
                await RunUninstallerAsync();
                break;

            case Step.Leftovers:
                await RemoveLeftoversAsync();
                break;

            case Step.Done:
                Close();
                break;
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        // На шаге со следами отказ означает «оставить как есть», а не отмену
        // уже выполненного удаления: деинсталлятор отработал и его не откатить.
        Close();
    }

    private async Task RunUninstallerAsync()
    {
        // Пока штатный деинсталлятор на месте, идём через него: он знает про свою
        // программу больше, чем можем узнать мы по косвенным признакам.
        if (ForcedUninstall.IsApplicable(_app))
        {
            _forced = true;
            await SearchLeftoversAsync();

            return;
        }

        EnterStep(Step.Running, Strings.Get("Uninstall.RunningTitle"), Strings.Get("Uninstall.RunningSubtitle"));

        BusyText.Text = Format.Text("Uninstall.Running", _app.DisplayName);

        var outcome = await new UninstallRunner()
            .RunAsync(_app, silent: false, TimeSpan.FromMinutes(30), CancellationToken.None);

        if (outcome.Status is UninstallStatus.Failed or UninstallStatus.TimedOut)
        {
            // Следы не ищем: программа, возможно, осталась на месте, и тогда её
            // рабочие файлы были бы предложены к удалению как остатки.
            ShowResult(
                Strings.Get("Uninstall.FailedTitle"),
                (outcome.Message ?? Strings.Get("Uninstall.FailedBody"))
                + Environment.NewLine + Environment.NewLine
                + Strings.Get("Uninstall.FailedNote"));

            return;
        }

        await SearchLeftoversAsync(outcome.Message);
    }

    private async Task SearchLeftoversAsync(string? uninstallerMessage = null)
    {
        EnterStep(Step.Running, Strings.Get("Uninstall.SearchingTitle"), Strings.Get("Uninstall.SearchingSubtitle"));
        BusyText.Text = Strings.Get("Uninstall.Searching");

        var found = await Task.Run(() => new LeftoverScanner().Scan(_app, CancellationToken.None));

        if (found.Count == 0)
        {
            var text = (uninstallerMessage is null ? string.Empty : uninstallerMessage + Environment.NewLine + Environment.NewLine)
                       + Strings.Get("Uninstall.NoTraces");

            // Следов нет, но запись в списке осталась — ради неё всё и затевалось.
            if (_forced)
            {
                text += Environment.NewLine + Environment.NewLine + new ForcedUninstall().RemoveRegistration(_app).Message;
            }

            ShowResult(Strings.Get("Uninstall.Done"), text);

            return;
        }

        _leftovers = found
            .OrderBy(f => f.Confidence)
            .ThenByDescending(f => f.SizeOnDiskBytes)
            .Select(LeftoverRow.From)
            .ToList();

        LeftoverItems.ItemsSource = _leftovers;

        var uncertain = _leftovers.Count(r => !r.IsChecked);

        LeftoverHint.Text = uncertain > 0
            ? Format.Text("Uninstall.HintUnchecked", _leftovers.Count - uncertain, _leftovers.Count)
            : Format.Text("Uninstall.HintAll", _leftovers.Count);

        EnterStep(Step.Leftovers, Strings.Get("Uninstall.LeftoversTitle"), Strings.Get("Uninstall.LeftoversSubtitle"));

        ActionButton.Content = Strings.Get("Uninstall.RemoveMarked");
        CancelButton.Content = Strings.Get("Uninstall.LeaveAsIs");
    }

    private async Task RemoveLeftoversAsync()
    {
        var selected = _leftovers.Where(r => r.IsChecked).Select(r => r.Item).ToList();

        if (selected.Count == 0)
        {
            ShowResult(Strings.Get("Uninstall.NothingRemoved"), Strings.Get("Uninstall.NothingChecked"));
            return;
        }

        var plan = new LeftoverPlanBuilder().Build(_app, selected);

        if (plan.TotalCount == 0)
        {
            ShowResult(Strings.Get("Uninstall.NothingLeft"), Strings.Get("Uninstall.VanishedWhileReading"));
            return;
        }

        var elevating = ElevatedExecution.WillAskForRights(plan, dryRun: false);

        EnterStep(
            Step.Running,
            Strings.Get("Uninstall.RemovingTitle"),
            Strings.Get(elevating ? "Uninstall.RemovingElevated" : "Uninstall.RemovingSubtitle"));

        BusyText.Text = Format.Text("Uninstall.Removing", selected.Count);

        var summary = await ElevatedExecution.RunAsync(plan, dryRun: false);
        var text = Summarize(summary);

        // Запись из списка убирается ПОСЛЕДНЕЙ. Убери её первой — и при сорвавшемся
        // удалении файлов программа исчезла бы из списка, оставшись на диске:
        // искать её следы было бы уже не от чего.
        if (_forced)
        {
            var outcome = new ForcedUninstall().RemoveRegistration(_app);

            text += Environment.NewLine + Environment.NewLine + outcome.Message;

            if (!outcome.Success)
            {
                text += Environment.NewLine + Strings.Get("Uninstall.StillListed");
            }
        }

        ShowResult(
            Strings.Get(summary.Succeeded > 0 || _forced ? "Uninstall.Done" : "Uninstall.NothingRemoved"),
            text);
    }

    private static string Summarize(RunSummary summary)
    {
        var lines = new List<string>();

        if (summary.Error is not null && summary.Succeeded == 0)
        {
            // Отказ в правах — решение человека, а не сбой программы.
            lines.Add(summary.Error);
            lines.Add(string.Empty);
            lines.Add(Strings.Get("Uninstall.RightsRefused"));

            return string.Join(Environment.NewLine, lines);
        }

        lines.Add(Format.Text("Uninstall.RemovedCount", summary.Succeeded));

        // Две цифры рядом — суть честного счётчика: заявленный размер и то,
        // насколько на самом деле изменилось свободное место.
        lines.Add(Format.Text(
            "Uninstall.FreedOf",
            Format.Size(summary.ActuallyFreedBytes),
            Format.Size(summary.ClaimedBytes)));

        if (summary.Elevated)
        {
            lines.Add(Strings.Get("Uninstall.ByElevated"));
        }

        if (summary.Skipped > 0)
        {
            lines.Add(Format.Text("Uninstall.Skipped", summary.Skipped));
        }

        if (summary.Failed > 0)
        {
            lines.Add(Format.Text("Uninstall.Failed", summary.Failed));
        }

        if (summary.Denied > 0)
        {
            lines.Add(Format.Text("Uninstall.Denied", summary.Denied));
        }

        if (summary.Error is not null)
        {
            lines.Add(summary.Error);
        }

        if (summary.Succeeded > 0 && summary.SessionId is not null)
        {
            lines.Add(string.Empty);
            lines.Add(Strings.Get("Uninstall.UndoHint"));
            lines.Add($"vacate-cli undo {summary.SessionId}");
        }

        if (summary.RegistryBackupPath is not null)
        {
            // Карантин реестр не покрывает, поэтому возврат идёт через файл.
            // Он открывается двойным щелчком и работает без этой программы.
            lines.Add(string.Empty);
            lines.Add(Strings.Get("Uninstall.BackupHint"));
            lines.Add(summary.RegistryBackupPath);
            lines.Add(Strings.Get("Uninstall.BackupHowTo"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void EnterStep(Step step, string title, string subtitle)
    {
        _step = step;

        StepTitle.Text = title;
        StepSubtitle.Text = subtitle;

        ConfirmPanel.Visibility = step == Step.Confirm ? Visibility.Visible : Visibility.Collapsed;
        BusyPanel.Visibility = step == Step.Running ? Visibility.Visible : Visibility.Collapsed;
        LeftoverPanel.Visibility = step == Step.Leftovers ? Visibility.Visible : Visibility.Collapsed;
        ResultPanel.Visibility = step == Step.Done ? Visibility.Visible : Visibility.Collapsed;

        // Пока идёт чужой деинсталлятор, нажимать нечего: кнопка, которая ничего
        // не делает, хуже отсутствующей — человек решит, что программа зависла.
        ActionButton.IsEnabled = step != Step.Running;
        CancelButton.IsEnabled = step != Step.Running;
        CancelButton.Visibility = step == Step.Done ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowResult(string title, string text)
    {
        EnterStep(Step.Done, title, Strings.Get("Uninstall.WhatHappened"));
        ResultText.Text = text;
        ActionButton.Content = Strings.Get("Common.Close");
    }
}

/// <summary>Строка списка следов с отметкой.</summary>
internal sealed class LeftoverRow
{
    private LeftoverRow(LeftoverItem item)
    {
        Item = item;

        // По умолчанию отмечено только то, за чем стоит больше, чем совпадение части имени.
        IsChecked = item.Confidence != LeftoverConfidence.Possible;
    }

    public LeftoverItem Item { get; }

    public bool IsChecked { get; set; }

    public string Path => Item.Path;

    public string Kind => Strings.Get(
        Item.Kind == LeftoverKind.Directory ? "Uninstall.Directory" : "Uninstall.RegistryKey");

    public string Size => Item.SizeOnDiskBytes > 0 ? Format.Size(Item.SizeOnDiskBytes) : string.Empty;

    public string Evidence => string.Join("  ·  ", Item.Evidence);

    public string ConfidenceLabel => Item.Confidence switch
    {
        LeftoverConfidence.Certain => Strings.Get("Uninstall.Certain"),
        LeftoverConfidence.Likely => Strings.Get("Uninstall.Likely"),
        _ => Strings.Get("Uninstall.Possible"),
    };

    public Brush ConfidenceBrush => Item.Confidence switch
    {
        LeftoverConfidence.Certain => Resource("AccentDimBrush"),
        LeftoverConfidence.Likely => Resource("AccentDimBrush"),
        _ => Resource("WarningBrush"),
    };

    public static LeftoverRow From(LeftoverItem item) => new(item);

    private static Brush Resource(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}
