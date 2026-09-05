using System.Diagnostics;
using System.IO;
using System.Text.Json;
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
            "Выберите программу и нажмите «Удалить». Несколько сразу — с зажатой клавишей Ctrl: тогда они удалятся одна за другой.",
            LoadAsync,
            extraButtonText: "Удалить программу",
            extraAction: UninstallSelectedAsync);

        AllowMultipleSelection();

        // Программу, которой не видно в списке, ищут по её окну: в списке она
        // называется так, как её назвал издатель, а не так, как написано в окне.
        AddSecondaryAction("Найти по окну", OpenHunter);
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
            Value: app.EstimatedSizeBytes > 0 ? Format.Size(app.EstimatedSizeBytes) : "размер неизвестен",
            Badge: app.LooksLikeRuntime ? "нужна другим" : app.Scope == InstallScope.User ? "только для вас" : null,
            Note: app.CanUninstall ? null : "Программа не сообщила системе, как её удалять",
            Payload: app))
            .ToList();

        var runtimes = apps.Count(a => a.LooksLikeRuntime);

        return (rows, $"Всего {apps.Count}, из них сред выполнения {runtimes}. Размер указан самой программой и часто занижен.");
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
                $"Выбрано программ: {apps.Count}.\n\n"
                + string.Join("\n", apps.Select(a => $"  · {a.DisplayName}"))
                + "\n\nОни будут удалены по очереди: для каждой откроется своё окно, "
                + "и каждый деинсталлятор задаст свои вопросы.\n\n"
                + "Прервать можно в любой момент — отменённые останутся на месте.\n\nПродолжить?",
                "Удаление нескольких программ",
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
            "Автозагрузка",
            "Выберите запись и нажмите кнопку, чтобы включить или отключить её. Критичные системные службы показаны, но переключить их нельзя.",
            LoadAsync,
            extraButtonText: "Включить или отключить",
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
                entry.Note ?? "Эту запись переключать нельзя: без неё система может перестать работать.",
                entry.Name,
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        var enable = !entry.IsEnabled;
        var action = enable ? "включить" : "отключить";

        var message = $"Автозапуск «{entry.Name}» будет {(enable ? "включён" : "отключён")}.\n\n";

        message += entry.Source switch
        {
            StartupSource.Service when !enable =>
                "Служба перейдёт в режим «вручную»: сама при загрузке не стартует, "
                + "но программа, которой она нужна, поднимет её по требованию.\n\n",

            StartupSource.StartupFolder =>
                "Ярлык будет переименован, а не удалён — вернуть можно тем же движением.\n\n",

            _ => "Запись не удаляется, а помечается — тем же способом, которым это делает "
                 + "диспетчер задач. Вернуть можно в любой момент.\n\n",
        };

        if (StartupToggle.RequiresElevation(entry))
        {
            message += "Запись общая для всех пользователей, поэтому Windows запросит права администратора.\n\n";
        }

        if (MessageBox.Show(message + "Продолжить?", $"Автозагрузка: {action}",
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
            return ToggleOutcome.Refused("Рядом с программой нет vacate-cli.exe — поставка неполная.");
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
                return ToggleOutcome.Refused("Не удалось запустить исполнителя.");
            }

            await process.WaitForExitAsync();

            return process.ExitCode == 0
                ? ToggleOutcome.Done(enable)
                : ToggleOutcome.Refused("Переключить запись не удалось. Подробности — в команде: vacate-cli startup");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Отказ в правах — решение человека, а не сбой.
            return ToggleOutcome.Refused("Вы отказались предоставить права администратора.");
        }
    }

    private static async Task<(IReadOnlyList<ListRow>, string)> LoadAsync(CancellationToken ct)
    {
        var entries = await Task.Run(() => new StartupScanner().Scan(ct), ct);

        var rows = entries.Select(e => new ListRow(
            Title: e.Name,
            Subtitle: e.ImagePath ?? e.Command,
            Value: e.IsEnabled ? "включено" : "выключено",
            Badge: e.Control == StartupControl.ViewOnly ? "только просмотр" : DescribeSource(e.Source),
            Note: e.Note,
            Payload: e))
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
            "Куда уходит место в вашей личной папке. Выберите крупный файл или группу копий и нажмите «Удалить» — всё уйдёт в Корзину.",
            LoadAsync,
            extraButtonText: "Удалить выбранное",
            extraAction: DeleteSelectedAsync);

        // Список говорит, что самое большое; карта — как это соотносится
        // между собой. Второй вопрос человек задаёт себе первым.
        AddSecondaryAction("Показать картой", OpenMap);
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
                $"Файл «{Path.GetFileName(file.Path)}» ({Format.Size(file.SizeOnDiskBytes)})"),

            DuplicateGroup group => (new DiskCleanupPlanBuilder().ForDuplicates([group]),
                $"Лишние копии: {group.Files.Count - 1} шт., освободится {Format.Size(group.RecoverableBytes)}"),

            _ => (null, string.Empty),
        };

        if (plan is null)
        {
            MessageBox.Show(
                "Эта строка — сводка по виду файлов, а не отдельный файл.\n\n"
                + "Выберите крупный файл или группу одинаковых копий.",
                "Удаление",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            return;
        }

        if (plan.TotalCount == 0)
        {
            MessageBox.Show("Файлы уже исчезли — список устарел. Обновите его.",
                "Удаление", MessageBoxButton.OK, MessageBoxImage.Information);

            return;
        }

        var message = description + "\n\n";

        if (Selected.Payload is DuplicateGroup keep)
        {
            // Какой файл останется, человек должен знать ДО нажатия.
            message += $"Останется: {keep.Files[0].Path}\n\n";
        }

        var inCloud = plan.AllOperations.OfType<DeleteFileOperation>()
            .Any(o => o.Target.Traits.HasFlag(FileTraits.InCloudFolder));

        if (inCloud)
        {
            message += "ВНИМАНИЕ: файл лежит в синхронизируемой папке. Он исчезнет "
                       + "на всех ваших устройствах, включая телефон, а Корзина вернёт его только здесь.\n\n";
        }

        message += "Всё удалённое уходит в Корзину.\n\nПродолжить?";

        var confirmed = MessageBox.Show(message, "Удаление файлов",
            MessageBoxButton.OKCancel, inCloud ? MessageBoxImage.Warning : MessageBoxImage.Question);

        if (confirmed != MessageBoxResult.OK)
        {
            return;
        }

        var summary = await ElevatedExecution.RunAsync(plan, dryRun: false);

        MessageBox.Show(
            summary.Error is not null && summary.Succeeded == 0
                ? summary.Error
                : $"Удалено: {summary.Succeeded}. Освободилось: {Format.Size(summary.ActuallyFreedBytes)}.\n\n"
                  + "Место вернётся полностью после очистки Корзины.",
            "Удаление файлов",
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
            rows.Add(new ListRow(category.Category, $"{category.FileCount} файлов", Format.Size(category.TotalBytes), "вид файлов"));
        }

        foreach (var file in result.LargestFiles.Take(15))
        {
            var synced = file.Traits.HasFlag(FileTraits.InCloudFolder);

            rows.Add(new ListRow(
                Path.GetFileName(file.Path),
                file.Path,
                Format.Size(file.SizeOnDiskBytes),
                synced ? "в облачной папке" : "крупный файл",
                synced ? "Удаление уйдёт на все ваши устройства" : null,
                Payload: file));
        }

        foreach (var group in result.Duplicates.Take(10))
        {
            var synced = group.Files.Any(f => f.Traits.HasFlag(FileTraits.InCloudFolder));

            rows.Add(new ListRow(
                $"{group.Files.Count} одинаковых копии",
                string.Join("   |   ", group.Files.Select(f => f.Path)),
                Format.Size(group.RecoverableBytes),
                synced ? "копии в облачной папке" : "дубликаты",
                synced
                    ? "Освободится столько, если оставить одну копию. Удаление уйдёт на все ваши устройства"
                    : "Освободится столько, если оставить одну копию",
                Payload: group));
        }

        var status = $"Просмотрено {result.TotalFilesScanned} файлов, {Format.Size(result.TotalBytesScanned)}."
                     + (result.RecoverableFromDuplicates > 0
                         ? $" На копиях можно вернуть {Format.Size(result.RecoverableFromDuplicates)}."
                         : string.Empty)
                     + (result.SkipNotes.Count > 0 ? $" Не вошло в подсчёт: {string.Join("; ", result.SkipNotes)}." : string.Empty);

        return (rows, status);
    }
}

/// <summary>Состояние системы: накопители и целостность системных файлов.</summary>
public sealed class HealthPage : ListPage
{
    public HealthPage()
    {
        Configure(
            "Состояние системы",
            "Показатели берутся у самих накопителей. Если диск их не сообщает, здесь будет честное «не сообщает», а не выдуманная оценка.",
            LoadAsync,
            extraButtonText: "Проверить целостность системы",
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
            "Windows проверит свои системные файлы и восстановит повреждённые.\n\n"
            + "Это занимает от 10 до 40 минут. Остановить проверку нельзя: закрытие её окна "
            + "работу не прервёт.\n\n"
            + "Откроется окно с ходом проверки, и Windows запросит права администратора.\n\n"
            + "Запустить?",
            "Проверка целостности",
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
                "Рядом с программой нет файла vacate-cli.exe, который выполняет проверку.\n\n"
                + "Похоже, поставка неполная — переустановите программу.",
                "Проверка целостности",
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
                  ?? "Проверка завершилась, но итог прочитать не удалось."
                : "Проверка завершилась, но итог не дошёл до программы.";
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            message = "Проверка завершилась, но итог прочитать не удалось.";
        }

        MessageBox.Show(message, "Проверка целостности", MessageBoxButton.OK, MessageBoxImage.Information);
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
