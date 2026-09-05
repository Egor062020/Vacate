using System.IO;
using System.Windows;
using Vacate.Abstractions.Model;
using Vacate.Platform.Windows.Files;
using Vacate.Platform.Windows.Registry;

namespace Vacate.App.Views;

/// <summary>Установленные программы.</summary>
public sealed class AppsPage : ListPage
{
    public AppsPage()
    {
        Configure(
            "Программы",
            "Выберите программу в списке и нажмите «Удалить». Среды выполнения помечены отдельно: от них зависят другие программы.",
            LoadAsync,
            extraButtonText: "Удалить программу",
            extraAction: UninstallSelectedAsync);
    }

    private static async Task<(IReadOnlyList<ListRow>, string)> LoadAsync(CancellationToken ct)
    {
        var apps = await Task.Run(() => new InstalledAppsScanner().Scan(ct), ct);

        var rows = apps.Select(app => new ListRow(
            Title: app.DisplayName,
            Subtitle: app.Publisher ?? string.Empty,
            Value: app.EstimatedSizeBytes > 0 ? Format.Size(app.EstimatedSizeBytes) : "размер неизвестен",
            Badge: app.LooksLikeRuntime ? "нужна другим" : app.Scope == InstallScope.User ? "только для вас" : null,
            Note: app.CanUninstall ? null : "Программа не сообщила системе, как её удалять",
            Payload: app))
            .ToList();

        var runtimes = apps.Count(a => a.LooksLikeRuntime);

        return (rows, $"Всего {apps.Count}, из них сред выполнения {runtimes}. Размер указан самой программой и часто занижен.");
    }

    /// <summary>Провести выбранную программу через удаление и зачистку следов.</summary>
    private async Task UninstallSelectedAsync()
    {
        if (Selected?.Payload is not InstalledApp app)
        {
            return;
        }

        var dialog = new UninstallWindow(app)
        {
            Owner = Window.GetWindow(this),
        };

        dialog.ShowDialog();

        // Список обязательно перечитывается: программы в нём больше может не быть,
        // и оставить её на экране означало бы показывать неправду.
        await LoadAsync();
    }
}

/// <summary>Автозапуск.</summary>
public sealed class StartupPage : ListPage
{
    public StartupPage()
    {
        Configure(
            "Автозагрузка",
            "Что запускается вместе с Windows. Критичные системные службы показаны, но отключить их нельзя.",
            LoadAsync);
    }

    private static async Task<(IReadOnlyList<ListRow>, string)> LoadAsync(CancellationToken ct)
    {
        var entries = await Task.Run(() => new StartupScanner().Scan(ct), ct);

        var rows = entries.Select(e => new ListRow(
            Title: e.Name,
            Subtitle: e.ImagePath ?? e.Command,
            Value: e.IsEnabled ? "включено" : "выключено",
            Badge: e.Control == StartupControl.ViewOnly ? "только просмотр" : DescribeSource(e.Source),
            Note: e.Note))
            .ToList();

        var programs = entries.Count(e => e.Source != StartupSource.Service);
        var services = entries.Count - programs;

        return (rows, $"Программ {programs}, служб {services}.");
    }

    private static string DescribeSource(StartupSource source) => source switch
    {
        StartupSource.RunKey => "реестр",
        StartupSource.StartupFolder => "папка автозагрузки",
        StartupSource.ScheduledTask => "задача",
        _ => "служба",
    };
}

/// <summary>Расширения браузеров.</summary>
public sealed class ExtensionsPage : ListPage
{
    public ExtensionsPage()
    {
        Configure(
            "Расширения браузеров",
            "Главное здесь — какие права расширение себе выпросило. Отключение делается в самом браузере: правку его настроек извне он отменяет при запуске.",
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
                Badge: e.ReadsAllSites ? "читает все сайты" : null,
                Note: string.Join("  ·  ", notable));
        }).ToList();

        var dangerous = extensions.Count(e => e.ReadsAllSites);

        var status = dangerous > 0
            ? $"Всего {extensions.Count}. С доступом ко всем сайтам: {dangerous} — такое расширение видит всё, что вы открываете."
            : $"Всего {extensions.Count}. Расширений с доступом ко всем сайтам нет.";

        return (rows, status);
    }
}

/// <summary>Место на диске.</summary>
public sealed class DiskPage : ListPage
{
    public DiskPage()
    {
        Configure(
            "Место на диске",
            "Куда уходит место в вашей личной папке: крупные файлы и одинаковые копии.",
            LoadAsync);
    }

    private static async Task<(IReadOnlyList<ListRow>, string)> LoadAsync(CancellationToken ct)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var result = await Task.Run(() => new DiskAnalyzer().Analyze(root, topCount: 25, ct), ct);

        var rows = new List<ListRow>();

        foreach (var category in result.ByCategory.Take(6))
        {
            rows.Add(new ListRow(category.Category, $"{category.FileCount} файлов", Format.Size(category.TotalBytes), "вид файлов"));
        }

        foreach (var file in result.LargestFiles.Take(15))
        {
            rows.Add(new ListRow(Path.GetFileName(file.Path), file.Path, Format.Size(file.SizeOnDiskBytes), "крупный файл"));
        }

        foreach (var group in result.Duplicates.Take(10))
        {
            rows.Add(new ListRow(
                $"{group.Files.Count} одинаковых копии",
                string.Join("   |   ", group.Files.Select(f => f.Path)),
                Format.Size(group.RecoverableBytes),
                "дубликаты",
                "Освободится столько, если оставить одну копию"));
        }

        var status = $"Просмотрено {result.TotalFilesScanned} файлов, {Format.Size(result.TotalBytesScanned)}."
                     + (result.RecoverableFromDuplicates > 0
                         ? $" На копиях можно вернуть {Format.Size(result.RecoverableFromDuplicates)}."
                         : string.Empty)
                     + (result.SkipNotes.Count > 0 ? $" Не вошло в подсчёт: {string.Join("; ", result.SkipNotes)}." : string.Empty);

        return (rows, status);
    }
}

/// <summary>Состояние дисков.</summary>
public sealed class HealthPage : ListPage
{
    public HealthPage()
    {
        Configure(
            "Состояние дисков",
            "Показатели берутся у самих накопителей. Если диск их не сообщает, здесь будет честное «не сообщает», а не выдуманная оценка.",
            LoadAsync);
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
                details.Add($"температура {temperature} °C");
            }

            if (disk.WearPercent is { } wear)
            {
                details.Add($"износ {wear}%");
            }

            if (disk.PowerOnHours is { } hours)
            {
                details.Add($"наработка {hours} ч");
            }

            var note = disk.Unavailable.Count > 0
                ? $"Диск не сообщает: {string.Join(", ", disk.Unavailable)}"
                : null;

            rows.Add(new ListRow(
                Title: disk.Model,
                Subtitle: $"{disk.MediaType} · {Format.Size(disk.SizeBytes)}" + (details.Count > 0 ? " · " + string.Join(" · ", details) : string.Empty),
                Value: DescribeHealth(disk.Health),
                Badge: disk.NeedsAttention ? "требует внимания" : null,
                Note: note));
        }

        var status = disks.Count == 0
            ? "Сведения о дисках недоступны. Часть данных требует прав администратора."
            : $"Дисков: {disks.Count}.";

        return (rows, status);
    }

    private static string DescribeHealth(DiskHealthStatus status) => status switch
    {
        DiskHealthStatus.Healthy => "исправен",
        DiskHealthStatus.Warning => "предупреждения",
        DiskHealthStatus.Unhealthy => "неисправен",
        _ => "не сообщил",
    };
}
