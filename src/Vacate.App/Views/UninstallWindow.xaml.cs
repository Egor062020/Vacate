using System.IO;
using System.Windows;
using System.Windows.Media;
using Vacate.Abstractions.Execution;
using Vacate.Abstractions.Model;
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
        StepTitle.Text = "Удаление программы";
        StepSubtitle.Text = "Проверьте, что это именно та программа. После запуска остановить деинсталлятор нельзя.";

        AppName.Text = _app.DisplayName;

        var details = new List<string>
        {
            $"Издатель:  {_app.Publisher ?? "не указан"}",
            $"Версия:    {_app.Version ?? "не указана"}",
            $"Каталог:   {_app.InstallLocation ?? "не указан"}",
            $"Установка: {(_app.Scope == InstallScope.User ? "только для вас" : "для всех пользователей")}",
        };

        AppDetails.Text = string.Join(Environment.NewLine, details);

        var warnings = new List<string>();

        if (_app.LooksLikeRuntime)
        {
            // Не запрет, а честное предупреждение: удалять компоненты иногда нужно,
            // но человек должен знать, что ломает не одну программу.
            warnings.Add("Похоже на компонент, нужный другим программам. После удаления те из них, "
                         + "что на него опираются, перестанут запускаться — и связь с этим действием "
                         + "будет уже не очевидна.");
        }

        if (!_app.CanUninstall)
        {
            warnings.Add("Программа не сообщила системе, как её удалять. Штатно удалить нечем — "
                         + "можно только поискать оставшиеся от неё файлы.");
        }

        if (_app.Scope == InstallScope.Machine)
        {
            warnings.Add("Программа установлена для всех пользователей: Windows запросит права администратора.");
        }

        if (warnings.Count > 0)
        {
            WarningCard.Visibility = Visibility.Visible;
            WarningText.Text = string.Join(Environment.NewLine + Environment.NewLine, warnings);
        }

        ActionButton.Content = _app.CanUninstall ? "Удалить программу" : "Поискать следы";
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
        if (!_app.CanUninstall)
        {
            await SearchLeftoversAsync();
            return;
        }

        EnterStep(Step.Running, "Работает деинсталлятор",
            "Отвечайте на его вопросы в его собственном окне. Vacate ждёт, пока он закончит.");

        BusyText.Text = $"Идёт удаление «{_app.DisplayName}»…";

        var outcome = await new UninstallRunner()
            .RunAsync(_app, silent: false, TimeSpan.FromMinutes(30), CancellationToken.None);

        if (outcome.Status is UninstallStatus.Failed or UninstallStatus.TimedOut)
        {
            // Следы не ищем: программа, возможно, осталась на месте, и тогда её
            // рабочие файлы были бы предложены к удалению как остатки.
            ShowResult("Удаление не завершилось",
                (outcome.Message ?? "Деинсталлятор не отработал.")
                + Environment.NewLine + Environment.NewLine
                + "Следы не искали: программа, возможно, осталась на месте, "
                + "и её рабочие файлы можно было бы принять за остатки.");

            return;
        }

        await SearchLeftoversAsync(outcome.Message);
    }

    private async Task SearchLeftoversAsync(string? uninstallerMessage = null)
    {
        EnterStep(Step.Running, "Ищу следы", "Смотрю каталоги программ и ветки реестра.");
        BusyText.Text = "Поиск следов…";

        var found = await Task.Run(() => new LeftoverScanner().Scan(_app, CancellationToken.None));

        if (found.Count == 0)
        {
            ShowResult("Готово",
                (uninstallerMessage is null ? string.Empty : uninstallerMessage + Environment.NewLine + Environment.NewLine)
                + "Программа удалена, следов не осталось.");

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
            ? $"Отмечено {_leftovers.Count - uncertain} из {_leftovers.Count}. Не отмечены совпадения уровня «возможно»: "
              + "за ними стоит только часть имени, и это может оказаться чужой каталог. Проверьте пути глазами."
            : $"Отмечено всё найденное: {_leftovers.Count}. Каталоги уйдут в карантин и вернутся кнопкой отката.";

        EnterStep(Step.Leftovers, "Что осталось после деинсталлятора",
            "Снимите отметку со всего, в чём не уверены. Удалено будет только отмеченное.");

        ActionButton.Content = "Удалить отмеченное";
        CancelButton.Content = "Оставить как есть";
    }

    private async Task RemoveLeftoversAsync()
    {
        var selected = _leftovers.Where(r => r.IsChecked).Select(r => r.Item).ToList();

        if (selected.Count == 0)
        {
            ShowResult("Ничего не удалено", "Не отмечено ни одного объекта. Программа удалена, следы остались на месте.");
            return;
        }

        var plan = new LeftoverPlanBuilder().Build(_app, selected);

        if (plan.TotalCount == 0)
        {
            ShowResult("Удалять нечего", "Отмеченные объекты исчезли, пока вы читали список.");
            return;
        }

        var elevating = ElevatedExecution.WillAskForRights(plan, dryRun: false);

        EnterStep(Step.Running, "Убираю следы",
            elevating
                ? "Часть следов лежит в системных папках: Windows сейчас спросит права администратора."
                : "Каталоги перемещаются в карантин, ветки реестра удаляются.");

        BusyText.Text = $"Удаление: {selected.Count} объектов…";

        var summary = await ElevatedExecution.RunAsync(plan, dryRun: false);

        ShowResult(summary.Succeeded > 0 ? "Готово" : "Ничего не удалено", Summarize(summary));
    }

    private static string Summarize(RunSummary summary)
    {
        var lines = new List<string>();

        if (summary.Error is not null && summary.Succeeded == 0)
        {
            // Отказ в правах — решение человека, а не сбой программы.
            lines.Add(summary.Error);
            lines.Add(string.Empty);
            lines.Add("Следы остались на месте. Программа при этом уже удалена.");

            return string.Join(Environment.NewLine, lines);
        }

        lines.Add($"Удалено объектов: {summary.Succeeded}");

        // Две цифры рядом — суть честного счётчика: заявленный размер и то,
        // насколько на самом деле изменилось свободное место.
        lines.Add($"Реально освободилось: {Format.Size(summary.ActuallyFreedBytes)} из заявленных {Format.Size(summary.ClaimedBytes)}");

        if (summary.Elevated)
        {
            lines.Add("Выполнено отдельным процессом с правами администратора.");
        }

        if (summary.Skipped > 0)
        {
            lines.Add($"Пропущено: {summary.Skipped}");
        }

        if (summary.Failed > 0)
        {
            lines.Add($"Не удалось удалить: {summary.Failed}");
        }

        if (summary.Denied > 0)
        {
            lines.Add($"Отклонено проверками безопасности: {summary.Denied}");
        }

        if (summary.Error is not null)
        {
            lines.Add(summary.Error);
        }

        if (summary.Succeeded > 0 && summary.SessionId is not null)
        {
            lines.Add(string.Empty);
            lines.Add("Каталоги лежат в карантине 30 дней. Вернуть их можно командой:");
            lines.Add($"vacate-cli undo {summary.SessionId}");
            lines.Add(string.Empty);
            lines.Add("Ветки реестра удалены без карантина — так честнее было сказано до нажатия.");
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
        EnterStep(Step.Done, title, "Что произошло:");
        ResultText.Text = text;
        ActionButton.Content = "Закрыть";
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

    public string Kind => Item.Kind == LeftoverKind.Directory ? "Каталог" : "Ветка реестра";

    public string Size => Item.SizeOnDiskBytes > 0 ? Format.Size(Item.SizeOnDiskBytes) : string.Empty;

    public string Evidence => string.Join("  ·  ", Item.Evidence);

    public string ConfidenceLabel => Item.Confidence switch
    {
        LeftoverConfidence.Certain => "точно её",
        LeftoverConfidence.Likely => "скорее всего её",
        _ => "возможно, чужое",
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
