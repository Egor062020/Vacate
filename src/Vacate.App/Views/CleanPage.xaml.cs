using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Vacate.Abstractions.Execution;
using Vacate.Abstractions.Model;
using Vacate.Core.Localization;
using Vacate.Abstractions.Safety;
using Vacate.Core.Execution;
using Vacate.Core.Journal;
using Vacate.Core.Safety;
using Vacate.Platform.Windows.Files;

namespace Vacate.App.Views;

public partial class CleanPage : UserControl
{
    private MutationPlan? _plan;
    private List<GroupRow> _rows = [];

    public CleanPage()
    {
        InitializeComponent();
    }

    private async void OnScan(object sender, RoutedEventArgs e)
    {
        SetBusy(true, Strings.Get("Clean.Scanning"));

        try
        {
            _plan = await Task.Run(() => BuildScanner().Scan(TempLocation.Standard(), CancellationToken.None));

            _rows = _plan.Groups
                .Select(g => new GroupRow(
                    g.GroupId,
                    DescribeGroup(g.Title),
                    g.RootPath ?? DescribePaths(g),
                    $"{Format.Size(g.SizeOnDiskBytes)}  ·  {g.Operations.Count} шт.",
                    DescribeConsequence(g),

                    // По умолчанию отмечено только безопасное. Кэш браузера человек
                    // включает сам, прочитав, чем это обернётся.
                    isChecked: g.MaxDeclaredRisk == RiskLevel.Green))
                .ToList();

            // Категории, которые окно без прав администратора не может даже перечислить.
            // Без этой строки они просто не появлялись в списке, и человек считал,
            // что системного мусора у него нет.
            _rows.AddRange(await Task.Run(FindUnreadableAsync));

            GroupsList.ItemsSource = _rows;

            StatusText.Text = _plan.TotalCount == 0
                ? Strings.Get("Clean.Empty")
                : Format.Text("Clean.Found", _plan.TotalCount, Format.Size(_plan.TotalSizeOnDiskBytes));

            PreviewButton.IsEnabled = _plan.TotalCount > 0;
            CleanButton.IsEnabled = _plan.TotalCount > 0;
        }
        finally
        {
            SetBusy(false, StatusText.Text);
        }
    }

    /// <summary>
    /// Где лежит категория, если каталог не один.
    /// </summary>
    /// <remarks>
    /// Показывается первый путь и число остальных. Голое «каталогов: 10» рядом
    /// с «10 шт.» читалось как одно и то же число про разные вещи, а перечислять
    /// шестнадцать путей кэша браузера незачем — они отличаются одним словом.
    /// </remarks>
    private static string DescribePaths(OperationGroup group)
    {
        var directories = group.Operations
            .OfType<DeleteFileOperation>()
            .Select(o => Path.GetDirectoryName(o.Target.Path))
            .Where(d => !string.IsNullOrEmpty(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (directories.Count == 0)
        {
            return string.Empty;
        }

        return directories.Count == 1
            ? directories[0]!
            : Format.Text("Clean.AndMore", directories[0], directories.Count - 1);
    }

    private static string DescribeConsequence(OperationGroup group) => group.GroupId switch
    {
        "cache.browsers" => Strings.Get("Clean.CatBrowsers"),
        "logs.windows" => Strings.Get("Clean.CatLogs"),
        "crash.reports" => Strings.Get("Clean.CatCrash"),
        "cache.delivery" => Strings.Get("Clean.CatDelivery"),
        _ => string.Empty,
    };

    /// <summary>
    /// Категории, существующие на машине, но недоступные окну без прав администратора.
    /// </summary>
    /// <remarks>
    /// Системный каталог временных файлов обычному пользователю не отдаёт даже список
    /// содержимого. Молчаливое отсутствие такой строки читается как «здесь чисто»,
    /// хотя на деле мы просто не смогли посмотреть.
    /// </remarks>
    private static List<GroupRow> FindUnreadableAsync()
    {
        var rows = new List<GroupRow>();

        foreach (var location in TempLocation.Standard())
        {
            foreach (var path in location.Paths)
            {
                if (!Directory.Exists(path))
                {
                    continue;
                }

                try
                {
                    // Достаточно одной попытки: если каталог не отдаёт даже первую запись,
                    // перечислить его целиком мы тем более не сможем.
                    _ = Directory.EnumerateFileSystemEntries(path).Any();
                }
                catch (UnauthorizedAccessException)
                {
                    rows.Add(new GroupRow(
                        location.Id,
                        DescribeGroup(LocalizedText.FromResource(location.TitleKey)),
                        path,
                        Strings.Get("Common.NeedsRights"),
                        Strings.Get("Clean.NeedsRightsNote"),
                        isChecked: false,
                        needsElevation: true));

                    break;
                }
            }
        }

        return rows;
    }

    /// <summary>План только из того, что человек оставил отмеченным.</summary>
    private MutationPlan? SelectedPlan()
    {
        if (_plan is null)
        {
            return null;
        }

        var chosen = _rows
            .Where(r => r.IsChecked && !r.NeedsElevation)
            .Select(r => r.GroupId)
            .ToHashSet(StringComparer.Ordinal);

        return _plan with { Groups = _plan.Groups.Where(g => chosen.Contains(g.GroupId)).ToList() };
    }

    /// <summary>
    /// Очистить категории, которые окно прочитать не может, — руками поднятого процесса.
    /// </summary>
    /// <remarks>
    /// Обычный путь здесь не годится: план строится по списку файлов, а список нам
    /// не отдают. Поэтому поднятый процесс сканирует и чистит сам, а сюда возвращает отчёт.
    /// </remarks>
    private async Task<RunSummary?> CleanUnreadableAsync()
    {
        var ids = _rows.Where(r => r.IsChecked && r.NeedsElevation).Select(r => r.GroupId).Distinct().ToList();

        if (ids.Count == 0)
        {
            return null;
        }

        var executor = Path.Combine(AppContext.BaseDirectory, "vacate-cli.exe");

        if (!File.Exists(executor))
        {
            return null;
        }

        var reportPath = Path.Combine(Path.GetTempPath(), $"vacate-clean-{Guid.NewGuid():N}.json");

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executor,
                Arguments = $"clean --only {string.Join(' ', ids)} --report \"{reportPath}\"",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            if (process is null)
            {
                return null;
            }

            await process.WaitForExitAsync();

            var report = File.Exists(reportPath)
                ? JsonSerializer.Deserialize<ElevatedRunReport>(await File.ReadAllTextAsync(reportPath))
                : null;

            return RunSummary.FromElevated(new ElevationOutcome(process.ExitCode == 0, "Выполнено", report));
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Отказ в правах — решение человека, а не сбой.
            return RunSummary.FromElevated(new ElevationOutcome(false, "Вы отказались предоставить права администратора"));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
        finally
        {
            try
            {
                if (File.Exists(reportPath))
                {
                    File.Delete(reportPath);
                }
            }
            catch (IOException)
            {
                // Останется во временной папке и уйдёт с обычной очисткой.
            }
        }
    }

    private async void OnPreview(object sender, RoutedEventArgs e) => await ExecuteAsync(dryRun: true);

    private async void OnClean(object sender, RoutedEventArgs e)
    {
        var plan = SelectedPlan();
        var hasElevated = _rows.Any(r => r.IsChecked && r.NeedsElevation);

        if ((plan is null || plan.TotalCount == 0) && !hasElevated)
        {
            MessageBox.Show(
                Strings.Get("Clean.NothingCheckedBody"),
                Strings.Get("Clean.ConfirmTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        var message = plan is { TotalCount: > 0 }
            ? Format.Text("Clean.ConfirmBody", plan.TotalCount, Format.Size(plan.TotalSizeOnDiskBytes))
            : Strings.Get("Clean.ConfirmElevatedOnly");

        // Отмеченные категории перечисляются поимённо: человек должен увидеть,
        // с чем именно он согласился, а не одну общую цифру.
        message += Strings.Get("Clean.WhatWillGo")
                   + string.Join("\n", _rows.Where(r => r.IsChecked).Select(r => $"  · {r.Title}"));

        // Окно системы с запросом прав появляется внезапно, и человек, не понимающий,
        // откуда оно, обычно нажимает «Нет». Предупреждаем заранее.
        if (hasElevated || (plan is not null && ElevatedExecution.WillAskForRights(plan, dryRun: false)))
        {
            message += Strings.Get("Clean.ElevationNote");
        }

        // Удаление безвозвратно — говорим это прямо и до, а не после.
        var confirmed = MessageBox.Show(
            message + "\n\n" + Strings.Get("Common.Continue"),
            Strings.Get("Clean.ConfirmTitle"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirmed == MessageBoxResult.OK)
        {
            await ExecuteAsync(dryRun: false);
        }
    }

    private async Task ExecuteAsync(bool dryRun)
    {
        var plan = SelectedPlan();
        var hasElevated = !dryRun && _rows.Any(r => r.IsChecked && r.NeedsElevation);

        if ((plan is null || plan.TotalCount == 0) && !hasElevated)
        {
            StatusText.Text = Strings.Get("Clean.NothingChecked");
            return;
        }

        SetBusy(true, Strings.Get(dryRun ? "Clean.DryRunning" : "Clean.Working"));

        try
        {
            var summary = plan is { TotalCount: > 0 }
                ? await ElevatedExecution.RunAsync(plan, dryRun)
                : null;

            // Категории, недоступные окну, чистит отдельный процесс. Итоги
            // складываются: человеку важна общая цифра, а не устройство работы.
            if (hasElevated && await CleanUnreadableAsync() is { } elevated)
            {
                summary = summary is null ? elevated : Merge(summary, elevated);
            }

            if (summary is not null)
            {
                ShowResult(summary);
            }
        }
        finally
        {
            SetBusy(false, string.Empty);
        }
    }

    private static RunSummary Merge(RunSummary first, RunSummary second) => first with
    {
        Succeeded = first.Succeeded + second.Succeeded,
        Skipped = first.Skipped + second.Skipped,
        Failed = first.Failed + second.Failed,
        Denied = first.Denied + second.Denied,
        ClaimedBytes = first.ClaimedBytes + second.ClaimedBytes,
        ActuallyFreedBytes = first.ActuallyFreedBytes + second.ActuallyFreedBytes,
        Elevated = first.Elevated || second.Elevated,

        // Сообщение второго прогона не теряется: отказ в правах должен быть виден,
        // даже если первая часть работы прошла успешно.
        Error = first.Error is null
            ? second.Error
            : second.Error is null ? first.Error : $"{first.Error}. {second.Error}",
    };

    private void ShowResult(RunSummary summary)
    {
        ResultCard.Visibility = Visibility.Visible;

        if (summary.Error is not null && summary.Succeeded == 0)
        {
            // Отказ в правах — не сбой, а решение человека. И звучать должно так же.
            ResultClaimed.Text = summary.Error;
            ResultActual.Text = Strings.Get("Clean.NothingChangedOnDisk");
            DiscrepancyList.ItemsSource = null;
            return;
        }

        if (summary.WasDryRun)
        {
            ResultClaimed.Text = Format.Text("Clean.WouldProcess", summary.Succeeded, Format.Size(summary.ClaimedBytes));
            ResultActual.Text = Strings.Get("Clean.NothingChangedOnDisk");
            DiscrepancyList.ItemsSource = null;
            return;
        }

        // Две цифры рядом. Конкуренты показывают только первую,
        // и она почти всегда больше действительной.
        ResultClaimed.Text = Format.Text("Clean.Deleted", summary.Succeeded, Format.Size(summary.ClaimedBytes))
                             + (summary.Elevated ? Strings.Get("Clean.ByElevated") : string.Empty);

        ResultActual.Text = Format.Text("Clean.ActuallyFreed", Format.Size(summary.ActuallyFreedBytes));

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
        DiscrepancyKind.HeldByProcess => Strings.Get("Clean.WhyHeld"),
        DiscrepancyKind.NotDeleted => Strings.Get("Clean.WhyNotDeleted"),
        DiscrepancyKind.HardLinked => Strings.Get("Clean.WhyHardLinked"),
        DiscrepancyKind.CompressedOrSparse => Strings.Get("Clean.WhyCompressed"),
        DiscrepancyKind.InQuarantine => Strings.Get("Clean.WhyQuarantine"),
        _ => Strings.Get("Clean.WhyRecycleBin"),
    };

    private void SetBusy(bool busy, string status)
    {
        ScanButton.IsEnabled = !busy;
        PreviewButton.IsEnabled = !busy && _plan is { TotalCount: > 0 };
        CleanButton.IsEnabled = !busy && _plan is { TotalCount: > 0 };

        SetScanIndicator(busy);

        if (!string.IsNullOrEmpty(status))
        {
            StatusText.Text = status;
        }
    }

    /// <summary>
    /// Показать, что работа идёт.
    /// </summary>
    /// <remarks>
    /// Сканирование занимает от секунд до минуты, и всё это время неподвижный экран
    /// читается как «зависло». Движение здесь — не украшение: оно единственное отличает
    /// работающую программу от повисшей.
    ///
    /// Системная настройка уменьшения анимации уважается: для того, кто её включил,
    /// движение на экране — не мелкое неудобство.
    /// </remarks>
    private void SetScanIndicator(bool busy)
    {
        ScanIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;

        if (!busy || !SystemParameters.ClientAreaAnimation)
        {
            ScanPulseShift.BeginAnimation(TranslateTransform.XProperty, null);
            return;
        }

        ScanPulseShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            From = 0,
            To = ScanIndicator.Width - ScanPulse.Width,
            Duration = TimeSpan.FromMilliseconds(900),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
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
        "Clean.Temp.User" => Strings.Get("Clean.Cat.TempUser"),
        "Clean.Temp.System" => Strings.Get("Clean.Cat.TempSystem"),
        "Clean.Logs.Windows" => Strings.Get("Clean.Cat.Logs"),
        "Clean.Cache.Browsers" => Strings.Get("Clean.Cat.Browsers"),
        "Clean.Crash.Reports" => Strings.Get("Clean.Cat.Crash"),
        "Clean.Cache.Delivery" => Strings.Get("Clean.Cat.Delivery"),
        _ => title.Translations?.GetValueOrDefault(Strings.IsEnglish ? "en" : "ru")
             ?? title.ResourceKey
             ?? Strings.Get("Clean.Cat.Other"),
    };

    /// <param name="isChecked">Категория попадёт в очистку. Меняется человеком.</param>
    /// <param name="needsElevation">
    /// Окно не смогло заглянуть в каталог: чистить будет отдельный процесс,
    /// который сам его и просканирует.
    /// </param>
    private sealed class GroupRow(
        string groupId,
        string title,
        string path,
        string sizeText,
        string consequence,
        bool isChecked,
        bool needsElevation = false)
    {
        public bool NeedsElevation { get; } = needsElevation;

        public string GroupId { get; } = groupId;

        public string Title { get; } = title;

        public string Path { get; } = path;

        public string SizeText { get; } = sizeText;

        public string Consequence { get; } = consequence;

        public bool IsChecked { get; set; } = isChecked;

        public Visibility ConsequenceVisibility =>
            string.IsNullOrEmpty(Consequence) ? Visibility.Collapsed : Visibility.Visible;
    }
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
