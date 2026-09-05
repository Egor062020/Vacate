using System.Text;
using System.Text.Json;
using Vacate.Abstractions.Execution;
using Vacate.Abstractions.Model;
using Vacate.Abstractions.Safety;
using Vacate.Cli;
using Vacate.Core.Execution;
using Vacate.Core.Journal;
using Vacate.Core.Safety;
using Vacate.Platform.Windows.Files;
using Vacate.Platform.Windows.Registry;

Console.OutputEncoding = Encoding.UTF8;

var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

try
{
    return command switch
    {
        "scan" => await Commands.ScanAsync(),
        "apps" => Commands.Apps(showRuntimes: args.Contains("--all")),
        "leftovers" => Commands.Leftovers(args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"))),
        "uninstall" => await Commands.UninstallAsync(
            args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")),
            silent: args.Contains("--silent"),
            assumeYes: args.Contains("--yes")),
        "startup" => Commands.Startup(args.Skip(1).ToArray()),
        "extensions" => Commands.Extensions(),
        "disk" => Commands.Disk(args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"))),
        "health" => Commands.Health(),
        "integrity" => await Commands.IntegrityAsync(
            reportPath: args.SkipWhile(a => a != "--report").Skip(1).FirstOrDefault()),
        "schedule" => Commands.Schedule(args.Skip(1).ToArray()),
        "--quiet-clean" => await Commands.QuietCleanAsync(),
        "--execute-plan" => await Commands.ExecutePlanAsync(
            args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")),
            reportPath: args.SkipWhile(a => a != "--report").Skip(1).FirstOrDefault()),
        "clean" => await Commands.CleanAsync(
            dryRun: args.Contains("--dry-run"),
            only: args.SkipWhile(a => a != "--only").Skip(1).TakeWhile(a => !a.StartsWith("--")).ToArray(),
            reportPath: args.SkipWhile(a => a != "--report").Skip(1).FirstOrDefault()),
        "history" => await Commands.HistoryAsync(),
        "undo" => await Commands.UndoAsync(args.Skip(1).FirstOrDefault()),
        _ => Commands.Help(),
    };
}
catch (OperationCanceledException)
{
    Console.WriteLine("Прервано.");
    return 130;
}
catch (Exception ex) when (command == "--execute-plan")
{
    // Единственная команда, у которой нет ни окна, ни консоли: её запускает
    // интерфейс с правами администратора и скрытым окном. Всё, что здесь упадёт,
    // человек увидел бы как «завершился с кодом -532462766» — поэтому причина
    // уходит в файл отчёта, откуда интерфейс её и покажет.
    await Commands.ReportFatalAsync(args.SkipWhile(a => a != "--report").Skip(1).FirstOrDefault(), ex);
    return 90;
}

namespace Vacate.Cli
{
    /// <summary>Команды консольной оболочки.</summary>
    internal static class Commands
    {
        private static string DataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vacate");

        public static int Help()
        {
            Console.WriteLine("""
                Vacate — очистка и обслуживание Windows.

                  vacate scan              показать, что найдено, ничего не меняя
                  vacate apps              установленные программы (--all: со средами выполнения)
                  vacate uninstall <имя>   удалить программу и убрать её следы
                  vacate leftovers <имя>   найти следы программы, ничего не удаляя
                  vacate startup           что стартует вместе с Windows (--all: и службы)
                  vacate startup off <id>  отключить автозапуск (on — включить обратно)
                  vacate extensions        расширения браузеров и их права
                  vacate disk <папка>      куда делось место: крупные файлы, дубли, виды
                  vacate health            состояние дисков
                  vacate integrity         проверка целостности системных файлов
                  vacate schedule          автоматическая очистка: status | on | off
                  vacate clean --dry-run   полный прогон без единого изменения на диске
                  vacate clean             выполнить очистку
                  vacate history           последние сеансы
                  vacate undo <сеанс>      вернуть то, что можно вернуть

                Временные файлы моложе суток не трогаются: их может использовать
                работающая программа.
                """);

            return 0;
        }

        public static async Task<int> ScanAsync()
        {
            var (scanner, _) = BuildScanner();
            var plan = scanner.Scan(TempLocation.Standard(), CancellationToken.None);

            if (plan.TotalCount == 0)
            {
                Console.WriteLine("Чисто. Мусор накапливается примерно за неделю, загляните позже.");
                return 0;
            }

            Console.WriteLine($"Найдено: {plan.TotalCount} объектов, {Format(plan.TotalSizeOnDiskBytes)}");
            Console.WriteLine();

            foreach (var group in plan.Groups)
            {
                Console.WriteLine($"  {Describe(group.Title),-32} {group.Operations.Count,8} шт.  {Format(group.SizeOnDiskBytes),12}");
            }

            Console.WriteLine();
            Console.WriteLine("Это оценка сверху: часть файлов может быть занята работающими программами.");
            return 0;
        }

        public static int Apps(bool showRuntimes)
        {
            var apps = new InstalledAppsScanner().Scan();

            // Среды выполнения занимают заметную часть списка и удалять их вслепую нельзя:
            // от них зависят другие программы. По умолчанию они не показываются,
            // но и не скрываются молча — счётчик внизу говорит, сколько их.
            var runtimes = apps.Where(a => a.LooksLikeRuntime).ToList();
            var visible = showRuntimes ? apps : apps.Where(a => !a.LooksLikeRuntime).ToList();

            Console.WriteLine($"Установлено программ: {visible.Count}");
            Console.WriteLine();

            foreach (var app in visible)
            {
                var size = app.EstimatedSizeBytes > 0 ? Format(app.EstimatedSizeBytes) : "—";
                var scope = app.Scope == InstallScope.User ? " (только для вас)" : string.Empty;
                var runtime = app.LooksLikeRuntime ? "  [нужна другим программам]" : string.Empty;
                var cannot = app.CanUninstall ? string.Empty : "  [нет команды удаления]";

                Console.WriteLine($"  {Trim(app.DisplayName, 44),-44} {Trim(app.Version, 14),-14} {size,10}{scope}{runtime}{cannot}");
            }

            if (!showRuntimes && runtimes.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"Скрыто сред выполнения: {runtimes.Count}. Показать: vacate apps --all");
                Console.WriteLine("От них зависят другие программы, поэтому удалять их вслепую нельзя.");
            }

            Console.WriteLine();
            Console.WriteLine("Размер указан по заявлению самой программы и часто занижен.");

            return 0;
        }

        public static int Schedule(string[] arguments)
        {
            var manager = new ScheduleManager();
            var action = arguments.FirstOrDefault()?.ToLowerInvariant() ?? "status";

            switch (action)
            {
                case "on":
                {
                    var executable = Environment.ProcessPath;

                    if (executable is null)
                    {
                        Console.WriteLine("Не удалось определить путь к программе.");
                        return 1;
                    }

                    var result = manager.Enable(executable, ScheduleFrequency.Weekly, atLogon: false);
                    Console.WriteLine(result.Message);

                    if (result.Success)
                    {
                        Console.WriteLine("Автоматически выполняются только безопасные категории:");
                        Console.WriteLine("временные файлы и кэши. Реестр и программы не затрагиваются никогда.");
                    }

                    return result.Success ? 0 : 1;
                }

                case "off":
                {
                    var result = manager.Disable();
                    Console.WriteLine(result.Message);
                    return result.Success ? 0 : 1;
                }

                default:
                {
                    var state = manager.GetState();

                    if (!state.Enabled)
                    {
                        Console.WriteLine("Автоматическая очистка выключена.");
                        Console.WriteLine("Включить: vacate schedule on");
                        return 0;
                    }

                    Console.WriteLine($"Автоматическая очистка включена, {state.Frequency}.");

                    if (state.NextRun is { } next)
                    {
                        Console.WriteLine($"Следующий запуск: {next:dd.MM.yyyy HH:mm}");
                    }

                    return 0;
                }
            }
        }

        /// <summary>
        /// Тихий режим для запуска по расписанию.
        /// </summary>
        /// <remarks>
        /// Выполняет только безопасные категории и ничего не спрашивает. Итог пишется
        /// в журнал, а не только в уведомление: уведомления скрываются режимом «не беспокоить»,
        /// и тогда результат ночной работы пропал бы бесследно.
        /// </remarks>
        public static async Task<int> QuietCleanAsync()
        {
            var (scanner, policy) = BuildScanner();
            var plan = scanner.Scan(TempLocation.Standard(), CancellationToken.None);

            if (plan.TotalCount == 0)
            {
                return 0;
            }

            var quarantine = new FileSystemQuarantine();
            var journal = new JsonlOperationJournal(Path.Combine(DataDirectory, "journal"));
            var volumes = new VolumeInfoProvider();

            var executor = new PlanExecutor(
                new RealEffectSink(quarantine),
                journal,
                volumes,
                new CurrentUserEnvironmentProvider(volumes),
                GuardSet.Group(policy),
                GuardSet.Item(),
                isDryRun: false);

            var report = await executor.ExecuteAsync(plan, null, CancellationToken.None);

            // Заодно убираем истёкший карантин: программа не висит в памяти,
            // и другого повода это сделать не будет.
            await quarantine.PurgeExpiredAsync(CancellationToken.None);

            return report.Failed > 0 ? 1 : 0;
        }

        /// <summary>
        /// Выполнить готовый план из файла. Служебная команда, вызываемая с правами
        /// администратора, когда интерфейсу этих прав не хватает.
        /// </summary>
        /// <remarks>
        /// Этот процесс работает с повышенными правами и берёт задание из файла,
        /// поэтому файл принимается не любой. Ограничения:
        ///
        ///   1. Только из временной папки текущего пользователя. Файл в общедоступном
        ///      каталоге мог бы подменить кто угодно, а выполнялся бы он под администратором.
        ///   2. Охрана применяется полностью, как и в любом другом запуске: единственный
        ///      шлюз не имеет режима «доверять вызывающему».
        ///
        /// Вывод идёт не на экран, а в файл отчёта: окно этого процесса скрыто,
        /// и печатать цифры было бы некуда — а интерфейсу нужны настоящие,
        /// иначе честный счётчик показал бы ноль после каждой операции с повышением.
        /// </remarks>
        public static async Task<int> ExecutePlanAsync(string? planPath, string? reportPath)
        {
            if (string.IsNullOrWhiteSpace(planPath) || !File.Exists(planPath))
            {
                await WriteReportAsync(reportPath, null, "Файл плана не найден");
                return 2;
            }

            if (!IsInsideUserTemp(planPath))
            {
                // Задание для процесса с правами администратора не может лежать там,
                // где его способен подменить другой пользователь.
                await WriteReportAsync(reportPath, null, "Файл плана лежит вне временной папки пользователя");
                return 3;
            }

            MutationPlan? plan;

            try
            {
                plan = JsonSerializer.Deserialize<MutationPlan>(await File.ReadAllTextAsync(planPath));
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
            {
                // Окно этого процесса скрыто: любое необработанное исключение здесь
                // выглядит для человека как «завершился с непонятным кодом». Поэтому
                // разбор ошибок широкий — в отчёт должна попасть причина, а не пустота.
                // Отдельно ловится NotSupportedException: именно им отвечает разбор JSON
                // на операцию без указания типа, и раньше он ронял процесс целиком.
                await WriteReportAsync(reportPath, null, $"План не удалось прочитать: {ex.Message}");
                return 4;
            }

            if (plan is null || plan.TotalCount == 0)
            {
                await WriteReportAsync(reportPath, null, "План пуст");
                return 5;
            }

            var (_, policy) = BuildScanner();
            var quarantine = new FileSystemQuarantine();
            var journal = new JsonlOperationJournal(Path.Combine(DataDirectory, "journal"));
            var volumes = new VolumeInfoProvider();

            var executor = new PlanExecutor(
                new RealEffectSink(quarantine),
                journal,
                volumes,
                new CurrentUserEnvironmentProvider(volumes),
                GuardSet.Group(policy),
                GuardSet.Item(),
                isDryRun: false);

            var report = await executor.ExecuteAsync(plan, null, CancellationToken.None);

            await WriteReportAsync(reportPath, report, null);

            return report.Failed > 0 ? 1 : 0;
        }

        /// <summary>Лежит ли файл во временной папке текущего пользователя.</summary>
        private static bool IsInsideUserTemp(string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);

                return full.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
        }

        /// <summary>Сообщить о сбое, который не удалось обработать по месту.</summary>
        public static Task ReportFatalAsync(string? reportPath, Exception exception)
            => WriteReportAsync(reportPath, null, $"Сбой при выполнении плана: {exception.Message}");

        private static async Task WriteReportAsync(string? reportPath, ExecutionReport? report, string? error)
        {
            if (string.IsNullOrWhiteSpace(reportPath))
            {
                return;
            }

            try
            {
                var payload = new ElevatedRunReport(
                    Succeeded: report?.Succeeded ?? 0,
                    Skipped: report?.Skipped ?? 0,
                    Failed: report?.Failed ?? 0,
                    Denied: report?.Denied ?? 0,
                    ClaimedBytes: report?.ClaimedBytes ?? 0,
                    ActuallyFreedBytes: report?.ActuallyFreedBytes ?? 0,
                    SessionId: report?.SessionId,
                    Error: error);

                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(payload));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Отчёт — удобство, а не условие выполнения. Код возврата всё равно дойдёт.
            }
        }

        /// <summary>
        /// Проверка целостности системных файлов.
        /// </summary>
        /// <remarks>
        /// Окно намеренно остаётся видимым, когда команду запускает интерфейс: проверка
        /// идёт от десяти минут до сорока, и человек, глядящий всё это время на неподвижную
        /// надпись, решает, что программа зависла. Штатная проверка печатает проценты сама —
        /// пусть печатает.
        ///
        /// Итог дополнительно уходит в файл, чтобы интерфейс мог показать его словами,
        /// а не заставлять читать журнал обслуживания.
        /// </remarks>
        public static async Task<int> IntegrityAsync(string? reportPath)
        {
            if (!SystemIntegrityChecker.IsElevated())
            {
                const string Message = "Проверка целостности возможна только с правами администратора.";

                Console.WriteLine(Message);
                await WriteIntegrityReportAsync(reportPath, IntegrityStatus.NeedsElevation, Message);

                return 2;
            }

            Console.WriteLine("Проверка целостности системных файлов.");
            Console.WriteLine("Занимает от 10 до 40 минут. Прервать её нельзя: закрытие этого окна");
            Console.WriteLine("проверку не остановит, она продолжит работать в фоне.");
            Console.WriteLine();

            var progress = new Progress<string>(Console.WriteLine);
            var result = await new SystemIntegrityChecker().RunAsync(progress, CancellationToken.None);

            Console.WriteLine();
            Console.WriteLine(result.Message);

            await WriteIntegrityReportAsync(reportPath, result.Status, result.Message);

            return result.Status switch
            {
                IntegrityStatus.Clean or IntegrityStatus.Repaired => 0,
                IntegrityStatus.DamageFound => 1,
                _ => 2,
            };
        }

        private static async Task WriteIntegrityReportAsync(string? reportPath, IntegrityStatus status, string message)
        {
            if (string.IsNullOrWhiteSpace(reportPath))
            {
                return;
            }

            try
            {
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new IntegrityReport(status.ToString(), message)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Отчёт — удобство. Итог уже напечатан в окне.
            }
        }

        public static int Health()
        {
            var disks = new DiskHealthReader().Read();

            if (disks.Count == 0)
            {
                Console.WriteLine("Не удалось получить сведения о дисках.");
                Console.WriteLine("Часть данных доступна только с правами администратора.");
                return 1;
            }

            foreach (var disk in disks)
            {
                Console.WriteLine($"{disk.Model}  ({disk.MediaType}, {Format(disk.SizeBytes)})");
                Console.WriteLine($"  Состояние:      {DescribeHealth(disk.Health)}");

                if (disk.TemperatureCelsius is { } temperature)
                {
                    Console.WriteLine($"  Температура:    {temperature} °C");
                }

                if (disk.WearPercent is { } wear)
                {
                    Console.WriteLine($"  Износ:          {wear}%");
                }

                if (disk.PowerOnHours is { } hours)
                {
                    Console.WriteLine($"  Наработка:      {hours} ч ({hours / 24 / 365.0:0.#} лет)");
                }

                if (disk.ReadErrorsTotal is { } errors && errors > 0)
                {
                    Console.WriteLine($"  Ошибок чтения:  {errors}");
                }

                // Молчание диска — это не «всё хорошо», и говорить об этом надо прямо.
                if (disk.Unavailable.Count > 0)
                {
                    Console.WriteLine($"  Диск не сообщает: {string.Join(", ", disk.Unavailable)}");
                }

                if (disk.NeedsAttention)
                {
                    Console.WriteLine("  ВНИМАНИЕ: показатели требуют проверки, сделайте резервную копию важных данных");
                }

                Console.WriteLine();
            }

            Console.WriteLine("Показатели берутся у самого диска. Если он их не сообщает,");
            Console.WriteLine("здесь будет честное «не сообщает», а не выдуманная оценка.");

            return 0;
        }

        private static string DescribeHealth(DiskHealthStatus status) => status switch
        {
            DiskHealthStatus.Healthy => "исправен",
            DiskHealthStatus.Warning => "есть предупреждения",
            DiskHealthStatus.Unhealthy => "неисправен",
            _ => "диск не сообщил (это не значит «всё хорошо»)",
        };

        public static int Disk(string? root)
        {
            root ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (!Directory.Exists(root))
            {
                Console.WriteLine($"Папка не найдена: {root}");
                return 1;
            }

            Console.WriteLine($"Анализирую: {root}");
            Console.WriteLine("Это может занять время на больших папках.");
            Console.WriteLine();

            var result = new DiskAnalyzer(new QuarantinePathCheck()).Analyze(root);

            Console.WriteLine($"Просмотрено файлов: {result.TotalFilesScanned}, всего {Format(result.TotalBytesScanned)}");
            Console.WriteLine();

            Console.WriteLine("Куда уходит место:");
            foreach (var category in result.ByCategory.Take(8))
            {
                Console.WriteLine($"  {category.Category,-30} {Format(category.TotalBytes),12}   {category.FileCount} шт.");
            }

            Console.WriteLine();
            Console.WriteLine("Самые большие папки:");
            foreach (var directory in result.LargestDirectories.Take(8))
            {
                Console.WriteLine($"  {Trim(Path.GetFileName(directory.Category), 40),-40} {Format(directory.TotalBytes),12}");
            }

            Console.WriteLine();
            Console.WriteLine("Самые большие файлы:");
            foreach (var file in result.LargestFiles.Take(8))
            {
                Console.WriteLine($"  {Trim(Path.GetFileName(file.Path), 46),-46} {Format(file.SizeOnDiskBytes),12}");
            }

            if (result.Duplicates.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"Одинаковые файлы (освободится {Format(result.RecoverableFromDuplicates)}):");

                foreach (var group in result.Duplicates.Take(5))
                {
                    Console.WriteLine($"  {group.Files.Count} копии по {Format(group.FileSizeBytes)}:");

                    foreach (var file in group.Files)
                    {
                        Console.WriteLine($"      {Trim(file.Path, 74)}");
                    }
                }
            }

            // Молчаливый пропуск читается пользователем как «этого нет».
            if (result.SkipNotes.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Что не вошло в подсчёт:");
                result.SkipNotes.ToList().ForEach(n => Console.WriteLine($"  · {n}"));
            }

            return 0;
        }

        public static int Extensions()
        {
            var extensions = new BrowserExtensionScanner().Scan();

            if (extensions.Count == 0)
            {
                Console.WriteLine("Расширений не найдено.");
                return 0;
            }

            Console.WriteLine($"Расширений установлено: {extensions.Count}");
            Console.WriteLine();

            // Сначала те, кто просит больше всего прав: именно их стоит пересмотреть.
            foreach (var extension in extensions)
            {
                var size = extension.SizeBytes > 0 ? Format(extension.SizeBytes) : string.Empty;
                var marker = extension.ReadsAllSites ? " ← читает все сайты" : string.Empty;

                Console.WriteLine($"  {Trim(extension.Name, 40),-40} {extension.Browser,-16} {size,10}{marker}");

                if (extension.ProfileName != "Default")
                {
                    Console.WriteLine($"      профиль: {extension.ProfileName}");
                }

                // Показываем только то, что действительно стоит внимания:
                // «хранит свои настройки» никому не интересно.
                var notable = extension.Permissions
                    .Where(p => p.Level >= PermissionLevel.SomeSites)
                    .DistinctBy(p => p.Description)
                    .Take(4);

                foreach (var permission in notable)
                {
                    Console.WriteLine($"      · {permission.Description}");
                }
            }

            Console.WriteLine();

            var dangerous = extensions.Count(e => e.ReadsAllSites);

            if (dangerous > 0)
            {
                Console.WriteLine($"Расширений с доступом ко всем сайтам: {dangerous}.");
                Console.WriteLine("Такое расширение видит всё, что вы открываете, включая банк и почту.");
            }

            Console.WriteLine("Отключение и удаление делаются в самом браузере: правку его настроек");
            Console.WriteLine("извне браузер отменяет при следующем запуске.");

            return 0;
        }

        public static int Startup(string[] arguments)
        {
            var action = arguments.FirstOrDefault()?.ToLowerInvariant();

            if (action is "on" or "off")
            {
                return ToggleStartup(arguments.Skip(1).FirstOrDefault(a => !a.StartsWith("--")), enable: action == "on");
            }

            return ListStartup(showAll: arguments.Contains("--all"));
        }

        /// <summary>Переключить одну запись автозапуска.</summary>
        private static int ToggleStartup(string? id, bool enable)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                Console.WriteLine("Укажите запись: vacate startup off <идентификатор>.");
                Console.WriteLine("Идентификаторы показывает vacate startup --all.");
                return 2;
            }

            var entry = new StartupScanner().Scan()
                .FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                Console.WriteLine($"Запись «{id}» не найдена.");
                return 1;
            }

            if (entry.IsEnabled == enable)
            {
                Console.WriteLine($"«{entry.Name}» уже {(enable ? "включена" : "отключена")}.");
                return 0;
            }

            var outcome = new StartupToggle().Set(entry, enable);

            if (!outcome.Success)
            {
                Console.WriteLine(outcome.Message ?? "Не удалось переключить запись.");

                if (StartupToggle.RequiresElevation(entry) && !SystemIntegrityChecker.IsElevated())
                {
                    Console.WriteLine("Эта запись общая для всех пользователей — запустите команду от имени администратора.");
                }

                return 1;
            }

            Console.WriteLine($"«{entry.Name}» {(enable ? "включена" : "отключена")}.");

            if (entry.Source == StartupSource.Service && !enable)
            {
                // Разница существенная, и человек должен о ней знать: иначе решит,
                // что отключение не сработало, увидев службу работающей.
                Console.WriteLine("Служба переведена в режим «вручную»: сама при загрузке не стартует,");
                Console.WriteLine("но программа, которой она нужна, поднимет её по требованию.");
            }

            return 0;
        }

        private static int ListStartup(bool showAll)
        {
            var entries = new StartupScanner().Scan();

            // Служб на обычной машине под сотню, и почти все системные.
            // Вываливать их сразу — значит утопить в них то, что человеку
            // действительно стоит посмотреть: программы, которые он ставил сам.
            var visible = showAll
                ? entries
                : entries.Where(e => e.Source != StartupSource.Service).ToList();

            var services = entries.Where(e => e.Source == StartupSource.Service).ToList();

            Console.WriteLine($"Записей автозапуска: {visible.Count}");
            Console.WriteLine();

            foreach (var group in visible.GroupBy(e => e.Source))
            {
                Console.WriteLine($"{DescribeSource(group.Key)}:");

                foreach (var entry in group)
                {
                    var state = entry.IsEnabled ? "вкл " : "выкл";
                    var scope = entry.Scope == InstallScope.User ? "вы" : "все";
                    var locked = entry.Control == StartupControl.ViewOnly ? "  [только просмотр]" : string.Empty;

                    Console.WriteLine($"  [{state}] {Trim(entry.Name, 38),-38} {scope,-4} {Trim(entry.ImagePath, 46)}{locked}");

                    // Идентификатор нужен для команды переключения, поэтому он виден,
                    // а не спрятан: иначе командой нельзя воспользоваться.
                    if (entry.Control == StartupControl.Toggleable)
                    {
                        Console.WriteLine($"         id: {entry.Id}");
                    }

                    if (entry.Note is not null)
                    {
                        Console.WriteLine($"         {entry.Note}");
                    }
                }

                Console.WriteLine();
            }

            if (!showAll && services.Count > 0)
            {
                var locked = services.Count(s => s.Control == StartupControl.ViewOnly);
                Console.WriteLine($"Служб в автозапуске: {services.Count}, из них защищённых от отключения: {locked}.");
                Console.WriteLine("Показать: vacate startup --all");
            }

            return 0;
        }

        private static string DescribeSource(StartupSource source) => source switch
        {
            StartupSource.RunKey => "Реестр, ключ автозапуска",
            StartupSource.StartupFolder => "Папка автозагрузки",
            StartupSource.ScheduledTask => "Задачи планировщика",
            _ => "Службы Windows",
        };

        public static int Leftovers(string? query)
        {
            var app = ResolveApp(query, "vacate leftovers <часть названия>");

            if (app is null)
            {
                return 2;
            }

            Console.WriteLine($"Следы программы «{app.DisplayName}»");

            var found = new LeftoverScanner().Scan(app);

            if (found.Count == 0)
            {
                Console.WriteLine("Ничего не найдено — программа не оставила заметных следов.");
                return 0;
            }

            PrintLeftovers(found);

            Console.WriteLine("Ничего не удалено: это только показ.");
            // Имя в кавычках: почти все названия многословны, и подсказка без кавычек
            // отправила бы человека в ошибку «подходит несколько программ».
            Console.WriteLine($"Удалить программу вместе со следами: vacate uninstall \"{app.DisplayName}\"");
            return 0;
        }

        /// <summary>
        /// Удаление программы: штатный деинсталлятор, затем зачистка следов.
        /// </summary>
        /// <remarks>
        /// Порядок именно такой и другим быть не может. Пока программа установлена,
        /// её каталог — не остаток, а рабочие файлы: удалив их первыми, мы оставили бы
        /// систему с записью в реестре, ведущей на исчезнувший деинсталлятор,
        /// то есть с программой, которую больше нечем удалить.
        ///
        /// Между шагами обязательно подтверждение. Оно не формальность: деинсталлятор
        /// чужой, и что именно он снесёт, мы не знаем до его запуска.
        /// </remarks>
        public static async Task<int> UninstallAsync(string? query, bool silent, bool assumeYes)
        {
            var app = ResolveApp(query, "vacate uninstall <часть названия>");

            if (app is null)
            {
                return 2;
            }

            Console.WriteLine($"Программа:  {app.DisplayName}");
            Console.WriteLine($"Издатель:   {app.Publisher ?? "не указан"}");
            Console.WriteLine($"Версия:     {app.Version ?? "не указана"}");
            Console.WriteLine($"Каталог:    {app.InstallLocation ?? "не указан"}");
            Console.WriteLine();

            if (app.LooksLikeRuntime)
            {
                // Не запрет, а честное предупреждение: удалять компоненты иногда нужно,
                // но человек должен знать, что ломает не одну программу.
                Console.WriteLine("ВНИМАНИЕ: похоже на компонент, нужный другим программам.");
                Console.WriteLine("После его удаления программы, которые на него опираются, перестанут запускаться.");
                Console.WriteLine();
            }

            if (!app.CanUninstall)
            {
                Console.WriteLine("Программа не сообщила системе, как её удалять.");
                Console.WriteLine("Штатно удалить нечем; можно поискать оставшиеся файлы: vacate leftovers <имя>.");
                return 1;
            }

            Console.WriteLine("Сейчас запустится деинсталлятор самой программы. Он чужой:");
            Console.WriteLine("что именно он удалит и о чём спросит, зависит от него, а не от Vacate.");
            Console.WriteLine();

            if (!Confirm("Запустить удаление?", assumeYes))
            {
                Console.WriteLine("Отменено. Ничего не изменилось.");
                return 0;
            }

            Console.WriteLine("Жду завершения деинсталлятора…");

            var outcome = await new UninstallRunner()
                .RunAsync(app, silent, TimeSpan.FromMinutes(30), CancellationToken.None);

            Console.WriteLine();

            if (outcome.Message is not null)
            {
                Console.WriteLine(outcome.Message);
            }

            if (outcome.Status is UninstallStatus.Failed or UninstallStatus.TimedOut)
            {
                Console.WriteLine("Следы не ищем: программа, возможно, осталась на месте.");
                return 1;
            }

            if (outcome.Status == UninstallStatus.Completed && outcome.Message is null)
            {
                Console.WriteLine("Деинсталлятор отработал.");
            }

            Console.WriteLine();
            return await CleanLeftoversAsync(app, assumeYes);
        }

        /// <summary>Найти и предложить к удалению то, что деинсталлятор не убрал.</summary>
        private static async Task<int> CleanLeftoversAsync(InstalledApp app, bool assumeYes)
        {
            Console.WriteLine("Ищу следы…");

            var found = new LeftoverScanner().Scan(app);

            if (found.Count == 0)
            {
                Console.WriteLine("Следов не осталось.");
                return 0;
            }

            PrintLeftovers(found);

            // Уровень «возможно» не отмечается никогда автоматически: за ним стоит
            // одно совпадение части имени, и ошибка стоит чужого каталога с данными.
            var proposed = found.Where(f => f.Confidence != LeftoverConfidence.Possible).ToList();
            var uncertain = found.Count - proposed.Count;

            if (proposed.Count == 0)
            {
                Console.WriteLine("К удалению ничего не предлагается: всё найденное — только возможные совпадения.");
                Console.WriteLine("Проверьте пути выше сами и удалите вручную, если это действительно следы программы.");
                return 0;
            }

            var size = proposed.Sum(p => p.SizeOnDiskBytes);

            Console.WriteLine($"К удалению предлагается: {proposed.Count} объектов, {Format(size)}.");

            if (uncertain > 0)
            {
                Console.WriteLine($"Ещё {uncertain} возможных совпадений НЕ предлагается — проверьте их сами.");
            }

            Console.WriteLine("Каталоги уйдут в карантин и вернутся командой отката. Ветки реестра — без карантина.");
            Console.WriteLine();

            if (!Confirm("Удалить предложенное?", assumeYes))
            {
                Console.WriteLine("Отменено. Ничего не изменилось.");
                return 0;
            }

            var plan = new LeftoverPlanBuilder().Build(app, proposed);

            if (plan.TotalCount == 0)
            {
                Console.WriteLine("Удалять нечего: объекты исчезли, пока вы читали список.");
                return 0;
            }

            var (_, policy) = BuildScanner();
            var quarantine = new FileSystemQuarantine();
            var journal = new JsonlOperationJournal(Path.Combine(DataDirectory, "journal"));
            var volumes = new VolumeInfoProvider();

            var executor = new PlanExecutor(
                new RealEffectSink(quarantine),
                journal,
                volumes,
                new CurrentUserEnvironmentProvider(volumes),
                GuardSet.Group(policy),
                GuardSet.Item(),
                isDryRun: false);

            var report = await executor.ExecuteAsync(plan, null, CancellationToken.None);

            Console.WriteLine();
            PrintReport(report);

            if (report.Succeeded > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"Вернуть удалённое: vacate undo {report.SessionId}");
            }

            return report.Failed > 0 ? 1 : 0;
        }

        /// <summary>Найти единственную программу по части названия.</summary>
        private static InstalledApp? ResolveApp(string? query, string usage)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                Console.WriteLine($"Укажите программу: {usage}. Список — vacate apps.");
                return null;
            }

            var matches = new InstalledAppsScanner().Scan()
                .Where(a => a.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

            switch (matches.Count)
            {
                case 0:
                    Console.WriteLine($"Программа с названием «{query}» не найдена.");
                    return null;

                case 1:
                    return matches[0];

                default:
                    // Угадывать нельзя: удалили бы не то, что имел в виду человек.
                    Console.WriteLine("Подходит несколько программ, уточните запрос:");
                    matches.ForEach(a => Console.WriteLine($"  {a.DisplayName}"));
                    return null;
            }
        }

        private static void PrintLeftovers(IReadOnlyList<LeftoverItem> found)
        {
            Console.WriteLine();

            // Порядок важен: сначала то, в чём мы уверены, в конце — спорное,
            // которое по умолчанию не отмечается к удалению.
            foreach (var group in found.GroupBy(f => f.Confidence).OrderBy(g => g.Key))
            {
                Console.WriteLine($"{DescribeConfidence(group.Key)}:");

                foreach (var item in group.OrderByDescending(i => i.SizeOnDiskBytes))
                {
                    var size = item.SizeOnDiskBytes > 0 ? Format(item.SizeOnDiskBytes) : string.Empty;
                    Console.WriteLine($"  {item.Path,-64} {size,10}");
                    Console.WriteLine($"      почему: {string.Join("; ", item.Evidence)}");
                }

                Console.WriteLine();
            }
        }

        /// <summary>
        /// Спросить подтверждение.
        /// </summary>
        /// <remarks>
        /// Ответом считается только явное «да». Нажатый Enter, пустая строка и любое
        /// невнятное слово означают отказ: цена ошибочного согласия здесь — чужие файлы.
        /// </remarks>
        private static bool Confirm(string question, bool assumeYes)
        {
            if (assumeYes)
            {
                Console.WriteLine($"{question} да (ключ --yes)");
                return true;
            }

            if (Console.IsInputRedirected)
            {
                // Спросить некого: команду запустили из сценария без ключа согласия.
                Console.WriteLine($"{question} нет — ввод недоступен. Для запуска из сценария добавьте --yes.");
                return false;
            }

            Console.Write($"{question} [да/нет] ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();

            return answer is "да" or "yes" or "y" or "д";
        }

        private static string DescribeConfidence(LeftoverConfidence confidence) => confidence switch
        {
            LeftoverConfidence.Certain => "Точно относится к программе",
            LeftoverConfidence.Likely => "Скорее всего относится к программе",
            _ => "Возможно, относится — проверьте сами (по умолчанию не отмечается)",
        };

        private static string Trim(string? value, int length)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "—";
            }

            return value.Length <= length ? value : value[..(length - 1)] + "…";
        }

        /// <summary>
        /// Очистка временных файлов.
        /// </summary>
        /// <param name="dryRun">Полный прогон без единого изменения на диске.</param>
        /// <param name="only">
        /// Идентификаторы категорий. Пустой список означает все. Ключ нужен интерфейсу:
        /// системные каталоги он без прав администратора даже перечислить не может,
        /// поэтому просит поднятый процесс просканировать и очистить их самому.
        /// </param>
        /// <param name="reportPath">Куда положить итог для вызывающего.</param>
        public static async Task<int> CleanAsync(bool dryRun, string[]? only = null, string? reportPath = null)
        {
            var (scanner, policy) = BuildScanner();

            var locations = only is { Length: > 0 }
                ? TempLocation.Standard().Where(l => only.Contains(l.Id, StringComparer.OrdinalIgnoreCase)).ToList()
                : TempLocation.Standard();

            var plan = scanner.Scan(locations, CancellationToken.None);

            if (plan.TotalCount == 0)
            {
                Console.WriteLine("Чисто, делать нечего.");
                await WriteReportAsync(reportPath, null, null);
                return 0;
            }

            Console.WriteLine(dryRun
                ? $"Пробный прогон: {plan.TotalCount} объектов, {Format(plan.TotalSizeOnDiskBytes)}. Ничего не изменится."
                : $"Очистка: {plan.TotalCount} объектов, {Format(plan.TotalSizeOnDiskBytes)}.");
            Console.WriteLine();

            var quarantine = new FileSystemQuarantine();
            var journal = new JsonlOperationJournal(Path.Combine(DataDirectory, "journal"));
            var volumes = new VolumeInfoProvider();

            // Весь механизм предпросмотра — в выборе приёмника действий.
            // Ни исполнитель, ни охрана не знают, в каком режиме работают.
            IEffectSink sink = dryRun ? new RecordingEffectSink() : new RealEffectSink(quarantine);

            var executor = new PlanExecutor(
                sink,
                journal,
                volumes,
                new CurrentUserEnvironmentProvider(volumes),
                GuardSet.Group(policy),
                GuardSet.Item(),
                dryRun);

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            var progress = new Progress<ExecutionProgress>(p =>
                Console.Write($"\r  {p.ProcessedCount}/{p.TotalCount}   {Format(p.FreedSoFarBytes)}   "));

            var report = await executor.ExecuteAsync(plan, progress, cts.Token);

            Console.WriteLine();
            Console.WriteLine();
            PrintReport(report);

            await WriteReportAsync(reportPath, report, null);

            return report.Cancelled ? 130 : 0;
        }

        public static async Task<int> HistoryAsync()
        {
            var journal = new JsonlOperationJournal(Path.Combine(DataDirectory, "journal"));
            var sessions = await journal.GetRecentSessionsAsync(10, CancellationToken.None);

            if (sessions.Count == 0)
            {
                Console.WriteLine("Сеансов пока не было.");
                return 0;
            }

            foreach (var session in sessions)
            {
                var restorable = session.HasRestorableItems ? "  можно откатить" : string.Empty;
                Console.WriteLine($"{session.SessionId}  {session.StartedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}  " +
                                  $"освобождено {Format(session.ActuallyFreedBytes),12}  объектов {session.ItemCount,7}{restorable}");
            }

            return 0;
        }

        public static async Task<int> UndoAsync(string? sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                Console.WriteLine("Укажите сеанс: vacate undo <идентификатор>. Список — vacate history.");
                return 2;
            }

            var journal = new JsonlOperationJournal(Path.Combine(DataDirectory, "journal"));
            var quarantine = new FileSystemQuarantine();

            var undoable = await journal.GetUndoableAsync(sessionId, CancellationToken.None);

            if (undoable.Count == 0)
            {
                Console.WriteLine("Возвращать нечего: в этом сеансе не было обратимых операций.");
                return 0;
            }

            var restored = 0;
            var failed = 0;

            foreach (var entry in undoable)
            {
                var result = await quarantine.RestoreAsync(entry.UndoToken, CancellationToken.None);

                if (result.Success)
                {
                    await journal.MarkRestoredAsync(sessionId, entry.UndoToken, CancellationToken.None);
                    restored++;
                }
                else
                {
                    failed++;
                    Console.WriteLine($"  не вернулось: {entry.OriginalPath}");
                }
            }

            Console.WriteLine($"Возвращено: {restored}. Не удалось: {failed}.");
            return failed == 0 ? 0 : 1;
        }

        private static (TempFilesScanner Scanner, PathPolicy Policy) BuildScanner()
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var systemDrive = Path.GetPathRoot(windows) ?? @"C:\";

            var ownDirectories = new List<string> { AppContext.BaseDirectory };
            ownDirectories.AddRange(FileSystemQuarantine.EnumerateStores());

            var policy = PathPolicy.CreateDefault(windows, systemDrive, ownDirectories);

            return (new TempFilesScanner(policy, new QuarantinePathCheck()), policy);
        }

        private static void PrintReport(ExecutionReport report)
        {
            if (report.WasDryRun)
            {
                Console.WriteLine($"Было бы обработано: {report.Succeeded} объектов, {Format(report.ClaimedBytes)}.");
                Console.WriteLine("На диске ничего не изменилось.");
            }
            else
            {
                // Две цифры рядом — суть честного счётчика.
                Console.WriteLine($"Удалено:             {report.Succeeded} объектов, {Format(report.ClaimedBytes)}");
                Console.WriteLine($"Реально освободилось: {Format(report.ActuallyFreedBytes)}");
            }

            if (report.Skipped > 0)
            {
                Console.WriteLine($"Пропущено: {report.Skipped}");
            }

            if (report.Failed > 0)
            {
                Console.WriteLine($"Не удалось: {report.Failed}");
            }

            if (report.Denied > 0)
            {
                Console.WriteLine($"Отклонено охраной: {report.Denied}");
            }

            if (report.Discrepancies.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Куда делась разница:");

                foreach (var reason in report.Discrepancies)
                {
                    Console.WriteLine($"  {Explain(reason.Kind),-46} {Format(reason.Bytes),12}" +
                                      (reason.Detail is null ? string.Empty : $"  ({reason.Detail})"));
                }
            }

            if (report.Cancelled)
            {
                Console.WriteLine();
                Console.WriteLine("Прервано. Всё, что успели сделать, записано в журнал.");
            }
        }

        private static string Explain(DiscrepancyKind kind) => kind switch
        {
            DiscrepancyKind.HeldByProcess => "занято работающей программой, вернётся при закрытии",
            DiscrepancyKind.NotDeleted => "не удалось удалить",
            DiscrepancyKind.HardLinked => "файл числится дважды, а занимает место один раз",
            DiscrepancyKind.CompressedOrSparse => "сжатые файлы: на диске занимали меньше",
            DiscrepancyKind.InQuarantine => "в карантине, освободится после истечения срока",
            DiscrepancyKind.InRecycleBin => "в Корзине, освободится после её очистки",
            _ => kind.ToString(),
        };

        private static string Describe(LocalizedText text)
        {
            // Тексты, встроенные в сборку, приходят ключом; тексты, собранные под конкретную
            // программу («Следы FreeCAD»), ключа иметь не могут и несут переводы с собой.
            if (text.Translations is { } translations)
            {
                return translations.TryGetValue("ru", out var russian) ? russian : translations.Values.First();
            }

            return text.ResourceKey switch
            {
                "Clean.Temp.User" => "Временные файлы пользователя",
                "Clean.Temp.System" => "Временные файлы системы",
                "Clean.Logs.Windows" => "Журналы Windows",
                "Clean.Cache.Browsers" => "Кэши браузеров",
                "Clean.Crash.Reports" => "Отчёты о сбоях программ",
                "Clean.Cache.Delivery" => "Загруженные обновления Windows",
                _ => text.ResourceKey ?? "—",
            };
        }

        /// <summary>
        /// Единицы двоичные, как в проводнике Windows.
        /// </summary>
        /// <remarks>
        /// Десятичные разошлись бы с проводником на семь процентов, и честный счётчик
        /// первым обвинили бы во лжи.
        /// </remarks>
        private static string Format(long bytes)
        {
            string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
            double value = bytes;
            var unit = 0;

            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit == 0 ? $"{bytes} {units[0]}" : $"{value:0.##} {units[unit]}";
        }

        private sealed class QuarantinePathCheck : IQuarantinePathCheck
        {
            public bool IsQuarantinePath(string path)
                => path.Contains(FileSystemQuarantine.DirectoryName, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Окружение охраны для консольного режима.
    /// </summary>
    /// <remarks>
    /// Пока берёт текущего пользователя процесса. Это верно, только когда программа
    /// запущена без повышения прав — то есть в том режиме, к которому продукт и переходит
    /// согласно описанию проекта. Определение целевого пользователя при запуске с чужими
    /// правами — задача отдельного этапа.
    /// </remarks>
    internal sealed class CurrentUserEnvironmentProvider(IVolumeInfoProvider volumes) : IGuardEnvironmentProvider
    {
        public GuardEnvironment Create()
        {
            var free = volumes.GetFreeSpaceByVolume();
            var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\";

            var emergency = free.TryGetValue(systemRoot, out var available)
                            && available < volumes.EmergencyThresholdBytes;

            return new GuardEnvironment(
                TargetUserSid: Environment.UserName,
                TargetUserProfilePath: Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                FreeSpaceByVolume: free,
                IsEmergencyMode: emergency,
                AdvancedMode: false);
        }
    }
}
