using System.IO;
using System.Windows;
using System.Windows.Controls;
using Vacate.Abstractions.Execution;
using Vacate.Abstractions.Model;
using Vacate.Abstractions.Safety;
using Vacate.Core.Execution;
using Vacate.Core.Journal;
using Vacate.Core.Safety;
using Vacate.Platform.Windows.Files;

namespace Vacate.App.Views;

public partial class CleanPage : UserControl
{
    private MutationPlan? _plan;

    public CleanPage()
    {
        InitializeComponent();
    }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Сканирую…");

        try
        {
            _plan = await Task.Run(() => BuildScanner().Scan(TempLocation.Standard(), CancellationToken.None));

            GroupsList.ItemsSource = _plan.Groups
                .Select(g => new GroupRow(
                    DescribeGroup(g.Title),
                    g.RootPath ?? string.Empty,
                    $"{Format.Size(g.SizeOnDiskBytes)}  ·  {g.Operations.Count} шт."))
                .ToList();

            StatusText.Text = _plan.TotalCount == 0
                ? "Чисто. Мусор накапливается примерно за неделю."
                : $"Найдено {_plan.TotalCount} объектов, {Format.Size(_plan.TotalSizeOnDiskBytes)}. Это оценка сверху: часть файлов может быть занята.";

            PreviewButton.IsEnabled = _plan.TotalCount > 0;
            CleanButton.IsEnabled = _plan.TotalCount > 0;
        }
        finally
        {
            SetBusy(false, StatusText.Text);
        }
    }

    private async void OnPreview(object sender, RoutedEventArgs e) => await ExecuteAsync(dryRun: true);

    private async void OnClean(object sender, RoutedEventArgs e)
    {
        var message = $"Будет удалено {_plan?.TotalCount ?? 0} объектов, около {Format.Size(_plan?.TotalSizeOnDiskBytes ?? 0)}.\n\n"
                      + "Это временные файлы и кэши: они создаются заново, но восстановить их будет нельзя.";

        // Окно системы с запросом прав появляется внезапно, и человек, не понимающий,
        // откуда оно, обычно нажимает «Нет». Предупреждаем заранее.
        if (_plan is not null && ElevatedExecution.WillAskForRights(_plan, dryRun: false))
        {
            message += "\n\nЧасть файлов лежит в системных папках, поэтому Windows спросит "
                       + "права администратора. Само окно программы этих прав не получает: "
                       + "работу выполнит отдельный процесс.";
        }

        // Удаление безвозвратно — говорим это прямо и до, а не после.
        var confirmed = MessageBox.Show(
            message + "\n\nПродолжить?",
            "Очистка",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirmed == MessageBoxResult.OK)
        {
            await ExecuteAsync(dryRun: false);
        }
    }

    private async Task ExecuteAsync(bool dryRun)
    {
        if (_plan is null)
        {
            return;
        }

        SetBusy(true, dryRun ? "Пробный прогон…" : "Очищаю…");

        try
        {
            var summary = await ElevatedExecution.RunAsync(_plan, dryRun);

            ShowResult(summary);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private void ShowResult(RunSummary summary)
    {
        ResultCard.Visibility = Visibility.Visible;

        if (summary.Error is not null && summary.Succeeded == 0)
        {
            // Отказ в правах — не сбой, а решение человека. И звучать должно так же.
            ResultClaimed.Text = summary.Error;
            ResultActual.Text = "На диске ничего не изменилось.";
            DiscrepancyList.ItemsSource = null;
            return;
        }

        if (summary.WasDryRun)
        {
            ResultClaimed.Text = $"Было бы обработано: {summary.Succeeded} объектов, {Format.Size(summary.ClaimedBytes)}.";
            ResultActual.Text = "На диске ничего не изменилось.";
            DiscrepancyList.ItemsSource = null;
            return;
        }

        // Две цифры рядом. Конкуренты показывают только первую,
        // и она почти всегда больше действительной.
        ResultClaimed.Text = $"Удалено: {summary.Succeeded} объектов, заявлено {Format.Size(summary.ClaimedBytes)}."
                             + (summary.Elevated ? " Выполнено процессом с правами администратора." : string.Empty);

        ResultActual.Text = $"Реально освободилось: {Format.Size(summary.ActuallyFreedBytes)}";

        var notes = summary.Discrepancies
            .Select(d => $"· {Explain(d.Kind)} — {Format.Size(d.Bytes)}" + (d.Detail is null ? string.Empty : $" ({d.Detail})"))
            .ToList();

        if (summary.Error is not null)
        {
            notes.Insert(0, $"· {summary.Error}");
        }

        DiscrepancyList.ItemsSource = notes;
    }

    private static string Explain(DiscrepancyKind kind) => kind switch
    {
        DiscrepancyKind.HeldByProcess => "занято работающей программой, вернётся при её закрытии",
        DiscrepancyKind.NotDeleted => "не удалось удалить",
        DiscrepancyKind.HardLinked => "файл числится дважды, а занимает место один раз",
        DiscrepancyKind.CompressedOrSparse => "сжатые файлы занимали на диске меньше",
        DiscrepancyKind.InQuarantine => "в карантине, освободится после истечения срока",
        _ => "в Корзине, освободится после её очистки",
    };

    private void SetBusy(bool busy, string status)
    {
        ScanButton.IsEnabled = !busy;
        PreviewButton.IsEnabled = !busy && _plan is { TotalCount: > 0 };
        CleanButton.IsEnabled = !busy && _plan is { TotalCount: > 0 };

        if (!string.IsNullOrEmpty(status))
        {
            StatusText.Text = status;
        }
    }

    private static TempFilesScanner BuildScanner() => new(BuildPolicy());

    /// <summary>Политика путей продукта. Общая для всех разделов, которые что-то удаляют.</summary>
    internal static PathPolicy BuildPolicy()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemDrive = Path.GetPathRoot(windows) ?? @"C:\";

        var own = new List<string> { AppContext.BaseDirectory };
        own.AddRange(FileSystemQuarantine.EnumerateStores());

        return PathPolicy.CreateDefault(windows, systemDrive, own);
    }

    private static string DescribeGroup(LocalizedText title) => title.ResourceKey switch
    {
        "Clean.Temp.User" => "Временные файлы пользователя",
        "Clean.Temp.System" => "Временные файлы системы",
        _ => title.ResourceKey ?? "Прочее",
    };

    private sealed record GroupRow(string Title, string Path, string SizeText);
}

/// <summary>Окружение охраны для оболочки.</summary>
internal sealed class UiEnvironmentProvider(IVolumeInfoProvider volumes) : IGuardEnvironmentProvider
{
    public GuardEnvironment Create()
    {
        var free = volumes.GetFreeSpaceByVolume();
        var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";

        return new GuardEnvironment(
            TargetUserSid: Environment.UserName,
            TargetUserProfilePath: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            FreeSpaceByVolume: free,
            IsEmergencyMode: free.TryGetValue(systemRoot, out var available) && available < volumes.EmergencyThresholdBytes,
            AdvancedMode: false);
    }
}
