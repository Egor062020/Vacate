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
        // Удаление безвозвратно — говорим это прямо и до, а не после.
        var confirmed = MessageBox.Show(
            $"Будет удалено {_plan?.TotalCount ?? 0} объектов, около {Format.Size(_plan?.TotalSizeOnDiskBytes ?? 0)}.\n\n" +
            "Это временные файлы и кэши: они создаются заново, но восстановить их будет нельзя.\n\n" +
            "Продолжить?",
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
            var report = await Task.Run(async () =>
            {
                var quarantine = new FileSystemQuarantine();
                var journal = new JsonlOperationJournal(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vacate", "journal"));
                var volumes = new VolumeInfoProvider();

                // Весь механизм предпросмотра — в выборе приёмника действий.
                IEffectSink sink = dryRun ? new RecordingEffectSink() : new RealEffectSink(quarantine);

                var executor = new PlanExecutor(
                    sink, journal, volumes,
                    new UiEnvironmentProvider(volumes),
                    [new EmergencyModeGuard(), new ProtectedPathGuard(BuildPolicy()), new RecycleBinOrderGuard(), new VolumeLimitGuard()],
                    [new ReparseAndCloudGuard()],
                    dryRun);

                return await executor.ExecuteAsync(_plan, null, CancellationToken.None);
            });

            ShowResult(report, dryRun);
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private void ShowResult(ExecutionReport report, bool dryRun)
    {
        ResultCard.Visibility = Visibility.Visible;

        if (dryRun)
        {
            ResultClaimed.Text = $"Было бы обработано: {report.Succeeded} объектов, {Format.Size(report.ClaimedBytes)}.";
            ResultActual.Text = "На диске ничего не изменилось.";
            DiscrepancyList.ItemsSource = null;
            return;
        }

        // Две цифры рядом. Конкуренты показывают только первую,
        // и она почти всегда больше действительной.
        ResultClaimed.Text = $"Удалено: {report.Succeeded} объектов, заявлено {Format.Size(report.ClaimedBytes)}.";
        ResultActual.Text = $"Реально освободилось: {Format.Size(report.ActuallyFreedBytes)}";

        DiscrepancyList.ItemsSource = report.Discrepancies
            .Select(d => $"· {Explain(d.Kind)} — {Format.Size(d.Bytes)}" + (d.Detail is null ? string.Empty : $" ({d.Detail})"))
            .ToList();
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

    private static PathPolicy BuildPolicy()
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
