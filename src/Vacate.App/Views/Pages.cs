using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Vacate.Abstractions.Model;
using Vacate.Core.Localization;
using Vacate.Platform.Windows.Files;
using Vacate.Platform.Windows.Registry;

namespace Vacate.App.Views;

/// <summary>Установленные программы.</summary>
public sealed class AppsPage : ListPage
{
    public AppsPage()
    {
        Configure(
            Strings.Get("Apps.Title"),
            Strings.Get("Apps.Subtitle"),
            LoadAsync,
            extraButtonText: Strings.Get("Apps.Uninstall"),
            extraAction: UninstallSelectedAsync);

        AllowMultipleSelection();

        // Программу, которой не видно в списке, ищут по её окну: в списке она
        // называется так, как её назвал издатель, а не так, как написано в окне.
        AddSecondaryAction(Strings.Get("Apps.Hunt"), OpenHunter);
    }

    private void OpenHunter()
    {
        var hunter = new HunterWindow { Owner = Window.GetWindow(this) };

        hunter.ShowDialog();
    }

    private static async Task<(IReadOnlyList<ListRow>, string)> LoadAsync(CancellationToken ct)
    {
        var apps = await Task.Run(() => new InstalledAppsScanner().Scan(ct), ct);

        var rows = apps.Select(app => new ListRow(
            Title: app.DisplayName,
            Subtitle: app.Publisher ?? string.Empty,
            Value: app.EstimatedSizeBytes > 0 ? Format.Size(app.EstimatedSizeBytes) : Strings.Get("Apps.UnknownSize"),
            Badge: app.LooksLikeRuntime ? Strings.Get("Apps.Runtime") : app.Scope == InstallScope.User ? Strings.Get("Apps.UserOnly") : null,
            Note: app.CanUninstall ? null : Strings.Get("Apps.NoUninstallCommand"),
            Payload: app))
            .ToList();

        var runtimes = apps.Count(a => a.LooksLikeRuntime);

        return (rows, Format.Text("Apps.Status", apps.Count, runtimes));
    }

    /// <summary>
    /// Провести выбранные программы через удаление и зачистку следов.
    /// </summary>
    /// <remarks>
    /// Пакет выполняется строго по одной программе за раз, с полным разговором о каждой.
    /// Соблазн спросить один раз и снести всё скопом велик, но деинсталляторы чужие:
    /// они задают собственные вопросы, требуют прав, иногда просят перезагрузку. Отвечать
    /// на них человек может только по одному.
    /// </remarks>
    private async Task UninstallSelectedAsync()
    {
        var apps = SelectedRows.Select(r => r.Payload).OfType<InstalledApp>().ToList();

        if (apps.Count == 0)
        {
            return;
        }

        if (apps.Count > 1)
        {
            var confirmed = MessageBox.Show(
                Format.Text(
                    "Apps.BatchBody",
                    apps.Count,
                    string.Join("\n", apps.Select(a => $"  · {a.DisplayName}"))),
                Strings.Get("Apps.BatchTitle"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (confirmed != MessageBoxResult.OK)
            {
                return;
            }
        }

        var owner = Window.GetWindow(this);

        foreach (var app in apps)
        {
            var dialog = new UninstallWindow(app) { Owner = owner };
            dialog.ShowDialog();
        }

        // Список обязательно перечитывается: программ в нём больше может не быть,
        // и оставить их на экране означало бы показывать неправду.
        await LoadAsync();
    }
}

/// <summary>Автозапуск.</summary>
public sealed class StartupPage : ListPage
{
    public StartupPage()
    {
        Configure(
            Strings.Get("Startup.Title"),
            Strings.Get("Startup.Subtitle"),
            LoadAsync,
            extraButtonText: Strings.Get("Startup.Toggle"),
            extraAction: ToggleSelectedAsync);
    }

    /// <summary>
    /// Переключить выбранную запись.
    /// </summary>
    /// <remarks>
    /// Ничего не удаляется: запись Run помечается в отдельной ветке, ярлык
    /// переименовывается, служба переводится в режим «вручную». Всё это обратимо
    /// тем же движением — человек, отключивший автозапуск, обычно хочет
    /// иметь возможность вернуть его.
    /// </remarks>
    private async Task ToggleSelectedAsync()
    {
        if (Selected?.Payload is not StartupEntry entry)
        {
            return;
        }

        if (entry.Control == StartupControl.ViewOnly)
        {
            MessageBox.Show(
                entry.Note ?? Strings.Get("Startup.CannotToggle"),
                entry.Name,
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        var enable = !entry.IsEnabled;
        var action = Strings.Get(enable ? "Startup.TurnOn" : "Startup.TurnOff");

        var message = Format.Text(enable ? "Startup.WillBeOn" : "Startup.WillBeOff", entry.Name);

        message += entry.Source switch
        {
            StartupSource.Service when !enable => Strings.Get("Startup.ServiceNote"),
            StartupSource.StartupFolder => Strings.Get("Startup.ShortcutNote"),
            _ => Strings.Get("Startup.RegistryNote"),
        };

        if (StartupToggle.RequiresElevation(entry))
        {
            message += Strings.Get("Startup.ElevationNote");
        }

        if (MessageBox.Show(message + Strings.Get("Common.Continue"), Format.Text("Startup.ConfirmTitle", action),
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        var outcome = StartupToggle.RequiresElevation(entry) && !SystemIntegrityChecker.IsElevated()
            ? await ToggleElevatedAsync(entry, enable)
            : await Task.Run(() => new StartupToggle().Set(entry, enable));

        if (!outcome.Success && outcome.Message is not null)
        {
            MessageBox.Show(outcome.Message, entry.Name, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Список перечитывается в любом случае: показывать прежнее состояние
        // после попытки его изменить — значит вводить в заблуждение.
        await LoadAsync();
    }

    /// <summary>Переключить запись руками отдельного процесса с правами администратора.</summary>
    private static async Task<ToggleOutcome> ToggleElevatedAsync(StartupEntry entry, bool enable)
    {
        var executor = Path.Combine(AppContext.BaseDirectory, "vacate-cli.exe");

        if (!File.Exists(executor))
        {
            return ToggleOutcome.Refused(Strings.Get("Settings.NoCli"));
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executor,
                Arguments = $"startup {(enable ? "on" : "off")} \"{entry.Id}\"",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            if (process is null)
            {
                return ToggleOutcome.Refused(Strings.Get("Startup.NoExecutor"));
            }

            await process.WaitForExitAsync();

            return process.ExitCode == 0
                ? ToggleOutcome.Done(enable)
                : ToggleOutcome.Refused(Strings.Get("Startup.ToggleFailed"));
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Отказ в правах — решение человека, а не сбой.
            return ToggleOutcome.Refused(Strings.Get("Startup.RightsRefused"));
        }
    }

    private static async Task<(IReadOnlyList<ListRow>, string)> LoadAsync(CancellationToken ct)
    {
        var entries = await Task.Run(() => new StartupScanner().Scan(ct), ct);

        var rows = entries.Select(e => new ListRow(
            Title: e.Name,
            Subtitle: e.ImagePath ?? e.Command,
            Value: e.IsEnabled ? Strings.Get("Startup.Enabled") : Strings.Get("Startup.Disabled"),
            Badge: e.Control == StartupControl.ViewOnly ? Strings.Get("Startup.ViewOnly") : DescribeSource(e.Source),
            Note: e.Note,
            Payload: e))
            .ToList();

        var programs = entries.Count(e => e.Source != StartupSource.Service);
        var services = entries.Count - programs;

        return (rows, Format.Text("Startup.Status", programs, services));
    }

    private static string DescribeSource(StartupSource source) => source switch
    {
        StartupSource.RunKey => Strings.Get("Startup.SourceRegistry"),
        StartupSource.StartupFolder => Strings.Get("Startup.SourceFolder"),
        StartupSource.ScheduledTask => Strings.Get("Startup.SourceTask"),
        _ => Strings.Get("Startup.SourceService"),
    };
}

/// <summary>Расширения браузеров.</summary>
public sealed class ExtensionsPage : ListPage
{
    public ExtensionsPage()
    {
        Configure(
            Strings.Get("Extensions.Title"),
            Strings.Get("Extensions.Subtitle"),
            LoadAsync);
    }

    private static async Task<(IReadOnlyList<ListRow>, string)> LoadAsync(CancellationToken ct)
    {
        var extensions = await Task.Run(() => new BrowserExtensionScanner().Scan(ct), ct);

        var rows = extensions.Select(e =>
        {
            var notable = e.Permissions
                .Where(p => p.Level >= PermissionLevel.SomeSites)
                .Select(p => p.Description)
                .Distinct()
                .Take(3);

            return new ListRow(
                Title: e.Name,
                Subtitle: $"{e.Browser} · {e.ProfileName}",
                Value: e.SizeBytes > 0 ? Format.Size(e.SizeBytes) : string.Empty,
                Badge: e.ReadsAllSites ? Strings.Get("Extensions.ReadsAllSites") : null,
                Note: string.Join("  ·  ", notable));
        }).ToList();

        var dangerous = extensions.Count(e => e.ReadsAllSites);

        var status = dangerous > 0
            ? Format.Text("Extensions.StatusDangerous", extensions.Count, dangerous)
            : Format.Text("Extensions.StatusSafe", extensions.Count);

        return (rows, status);
    }
}

/// <summary>Место на диске.</summary>
public sealed class DiskPage : ListPage
{
    public DiskPage()
    {
        Configure(
            Strings.Get("Disk.Title"),
            Strings.Get("Disk.Subtitle"),
            LoadAsync,
            extraButtonText: Strings.Get("Disk.Delete"),
            extraAction: DeleteSelectedAsync);

        // Список говорит, что самое большое; карта — как это соотносится
        // между собой. Второй вопрос человек задаёт себе первым.
        AddSecondaryAction(Strings.Get("Disk.ShowMap"), OpenMap);
    }

    private void OpenMap()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var map = new DiskMapWindow(root) { Owner = Window.GetWindow(this) };

        map.ShowDialog();
    }

    /// <summary>
    /// Удалить выбранный файл или лишние копии.
    /// </summary>
    /// <remarks>
    /// Единственное место в продукте, где удаляются личные файлы человека, а не служебные.
    /// Поэтому Корзина вместо карантина: туда он привык заглядывать сам и вернёт файл
    /// без нашей помощи — даже если программа к тому времени удалена.
    /// </remarks>
    private async Task DeleteSelectedAsync()
    {
        if (Selected is null)
        {
            return;
        }

        var (plan, description) = Selected.Payload switch
        {
            ScannedFile file => (new DiskCleanupPlanBuilder().ForFiles([file]),
                Format.Text("Disk.OneFile", Path.GetFileName(file.Path), Format.Size(file.SizeOnDiskBytes))),

            DuplicateGroup group => (new DiskCleanupPlanBuilder().ForDuplicates([group]),
                Format.Text("Disk.ExtraCopies", group.Files.Count - 1, Format.Size(group.RecoverableBytes))),

            _ => (null, string.Empty),
        };

        if (plan is null)
        {
            MessageBox.Show(
                Strings.Get("Disk.NotAFile"),
                Strings.Get("Disk.DeleteTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        if (plan.TotalCount == 0)
        {
            MessageBox.Show(Strings.Get("Disk.ListStale"),
                Strings.Get("Disk.DeleteTitle"), MessageBoxButton.OK, MessageBoxImage.Information);

            return;
        }

        var message = description + "\n\n";

        if (Selected.Payload is DuplicateGroup keep)
        {
            // Какой файл останется, человек должен знать ДО нажатия.
            message += Format.Text("Disk.WillKeep", keep.Files[0].Path);
        }

        var inCloud = plan.AllOperations.OfType<DeleteFileOperation>()
            .Any(o => o.Target.Traits.HasFlag(FileTraits.InCloudFolder));

        if (inCloud)
        {
            message += Strings.Get("Disk.CloudWarning");
        }

        message += Strings.Get("Disk.ToRecycleBin");

        var confirmed = MessageBox.Show(message, Strings.Get("Disk.DeleteTitle"),
            MessageBoxButton.OKCancel, inCloud ? MessageBoxImage.Warning : MessageBoxImage.Question);

        if (confirmed != MessageBoxResult.OK)
        {
            return;
        }

        var summary = await ElevatedExecution.RunAsync(plan, dryRun: false);

        MessageBox.Show(
            summary.Error is not null && summary.Succeeded == 0
                ? summary.Error
                : Format.Text("Disk.DeleteResult", summary.Succeeded, Format.Size(summary.ActuallyFreedBytes)),
            Strings.Get("Disk.DeleteTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        await LoadAsync();
    }

    private static async Task<(IReadOnlyList<ListRow>, string)> LoadAsync(CancellationToken ct)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var result = await Task.Run(() => new DiskAnalyzer().Analyze(root, topCount: 25, ct), ct);

        var rows = new List<ListRow>();

        foreach (var category in result.ByCategory.Take(6))
        {
            rows.Add(new ListRow(
                category.Category,
                Format.Text("Disk.FilesCount", category.FileCount),
                Format.Size(category.TotalBytes),
                Strings.Get("Disk.FileKind")));
        }

        foreach (var file in result.LargestFiles.Take(15))
        {
            var synced = file.Traits.HasFlag(FileTraits.InCloudFolder);

            rows.Add(new ListRow(
                Path.GetFileName(file.Path),
                file.Path,
                Format.Size(file.SizeOnDiskBytes),
                synced ? Strings.Get("Disk.InCloud") : Strings.Get("Disk.LargeFile"),
                synced ? Strings.Get("Disk.CloudNote") : null,
                Payload: file));
        }

        foreach (var group in result.Duplicates.Take(10))
        {
            var synced = group.Files.Any(f => f.Traits.HasFlag(FileTraits.InCloudFolder));

            rows.Add(new ListRow(
                Format.Text("Disk.CopiesCount", group.Files.Count),
                string.Join("   |   ", group.Files.Select(f => f.Path)),
                Format.Size(group.RecoverableBytes),
                synced ? Strings.Get("Disk.InCloud") : Strings.Get("Disk.Duplicates"),
                synced
                    ? Strings.Get("Disk.CopiesCloudNote")
                    : Strings.Get("Disk.CopiesNote"),
                Payload: group));
        }

        var status = Format.Text("Disk.Status", result.TotalFilesScanned, Format.Size(result.TotalBytesScanned))
                     + (result.RecoverableFromDuplicates > 0
                         ? Format.Text("Disk.StatusDuplicates", Format.Size(result.RecoverableFromDuplicates))
                         : string.Empty)
                     + (result.SkipNotes.Count > 0 ? Format.Text("Disk.StatusSkipped", string.Join("; ", result.SkipNotes)) : string.Empty);

        return (rows, status);
    }
}

/// <summary>Состояние системы: накопители и целостность системных файлов.</summary>
public sealed class HealthPage : ListPage
{
    public HealthPage()
    {
        Configure(
            Strings.Get("Health.Title"),
            Strings.Get("Health.Subtitle"),
            LoadAsync,
            extraButtonText: Strings.Get("Health.CheckIntegrity"),
            extraAction: CheckIntegrityAsync,
            requiresSelection: false);
    }

    /// <summary>
    /// Запустить штатную проверку целостности системных файлов.
    /// </summary>
    /// <remarks>
    /// Проверка требует прав администратора, которых у окна программы нет намеренно,
    /// поэтому её выполняет отдельный процесс. Его окно остаётся видимым: проверка идёт
    /// от десяти минут до сорока, и человек, глядящий всё это время на неподвижную полосу,
    /// решит, что программа зависла, — а прервать проверку всё равно нельзя.
    /// </remarks>
    private async Task CheckIntegrityAsync()
    {
        var confirmed = MessageBox.Show(
            Strings.Get("Integrity.Confirm"),
            Strings.Get("Integrity.Title"),
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirmed != MessageBoxResult.OK)
        {
            return;
        }

        var executor = Path.Combine(AppContext.BaseDirectory, "vacate-cli.exe");

        if (!File.Exists(executor))
        {
            MessageBox.Show(
                Strings.Get("Integrity.NoCli"),
                Strings.Get("Integrity.Title"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);

            return;
        }

        var reportPath = Path.Combine(Path.GetTempPath(), $"vacate-integrity-{Guid.NewGuid():N}.json");

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executor,
                Arguments = $"integrity --report \"{reportPath}\"",

                // Запрос прав через оболочку: система показывает своё штатное окно.
                UseShellExecute = true,
                Verb = "runas",
            });

            if (process is null)
            {
                return;
            }

            await process.WaitForExitAsync();

            ShowIntegrityOutcome(reportPath);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Отказ в правах — решение человека, а не сбой. Молчим.
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
                // Останется во временной папке и будет убран обычной очисткой.
            }
        }
    }

    private static void ShowIntegrityOutcome(string reportPath)
    {
        string message;

        try
        {
            message = File.Exists(reportPath)
                ? JsonSerializer.Deserialize<IntegrityReport>(File.ReadAllText(reportPath))?.Message
                  ?? Strings.Get("Integrity.NoResult")
                : Strings.Get("Integrity.NoReport");
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            message = Strings.Get("Integrity.NoResult");
        }

        MessageBox.Show(message, Strings.Get("Integrity.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static async Task<(IReadOnlyList<ListRow>, string)> LoadAsync(CancellationToken ct)
    {
        var disks = await Task.Run(() => new DiskHealthReader().Read(ct), ct);

        var rows = new List<ListRow>();

        foreach (var disk in disks)
        {
            var details = new List<string>();

            if (disk.TemperatureCelsius is { } temperature)
            {
                details.Add(Format.Text("Health.Temperature", temperature));
            }

            if (disk.WearPercent is { } wear)
            {
                details.Add(Format.Text("Health.Wear", wear));
            }

            if (disk.PowerOnHours is { } hours)
            {
                details.Add(Format.Text("Health.PowerOn", hours));
            }

            var note = disk.Unavailable.Count > 0
                ? Format.Text("Health.NotReported", string.Join(", ", disk.Unavailable))
                : null;

            rows.Add(new ListRow(
                Title: disk.Model,
                Subtitle: $"{disk.MediaType} · {Format.Size(disk.SizeBytes)}" + (details.Count > 0 ? " · " + string.Join(" · ", details) : string.Empty),
                Value: DescribeHealth(disk.Health),
                Badge: disk.NeedsAttention ? Strings.Get("Health.NeedsAttention") : null,
                Note: note));
        }

        var status = disks.Count == 0
            ? Strings.Get("Health.NoDisks")
            : Format.Text("Health.DiskCount", disks.Count);

        return (rows, status);
    }

    private static string DescribeHealth(DiskHealthStatus status) => status switch
    {
        DiskHealthStatus.Healthy => Strings.Get("Health.Healthy"),
        DiskHealthStatus.Warning => Strings.Get("Health.Warning"),
        DiskHealthStatus.Unhealthy => Strings.Get("Health.Unhealthy"),
        _ => Strings.Get("Health.Unknown"),
    };
}
