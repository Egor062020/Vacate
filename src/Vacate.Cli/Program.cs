using System.Text;
using System.Text.Json;
using Vacate.Abstractions.Execution;
using Vacate.Abstractions.Model;
using Vacate.Abstractions.Safety;
using Vacate.Cli;
using Vacate.Core.Execution;
using Vacate.Core.Journal;
using Vacate.Core.Localization;
using Vacate.Core.Safety;
using Vacate.Platform.Windows.Files;
using Vacate.Platform.Windows.Registry;

Console.OutputEncoding = Encoding.UTF8;

// Язык берётся из той же настройки, что и в окне программы: два инструмента
// одного продукта, говорящие на разных языках, выглядят как чужие друг другу.
Strings.Use(AppSettings.Load().Language);

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
        "restore-point" => Commands.CreateRestorePoint(),
        "watch" => Commands.Watch(args.Skip(1).ToArray()),
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
    Console.WriteLine(Strings.Get("Cli.Interrupted"));
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
        /// <summary>Короткое имя для перевода: в этом файле оно встречается сотни раз.</summary>
        private static string S(string key) => Strings.Get(key);

        /// <summary>Переведённый текст с подстановками.</summary>
        private static string S(string key, params object?[] args) =>
            string.Format(System.Globalization.CultureInfo.CurrentCulture, Strings.Get(key), args);

        private static string DataDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vacate");

        public static int Help()
        {
            Console.WriteLine(S("Cli.Help"));

            return 0;
        }

        public static async Task<int> ScanAsync()
        {
            var (scanner, _) = BuildScanner();
            var plan = scanner.Scan(TempLocation.Standard(), CancellationToken.None);

            if (plan.TotalCount == 0)
            {
                Console.WriteLine(S("Cli.Clean"));
                return 0;
            }

            Console.WriteLine(S("Cli.Found", plan.TotalCount, Format(plan.TotalSizeOnDiskBytes)));
            Console.WriteLine();

            foreach (var group in plan.Groups)
            {
                Console.WriteLine($"  {Describe(group.Title),-32} {group.Operations.Count,8} {S("Cli.Items")}  {Format(group.SizeOnDiskBytes),12}");
            }

            Console.WriteLine();
            Console.WriteLine(S("Cli.UpperBound"));
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

            Console.WriteLine(S("Cli.AppsCount", visible.Count));
            Console.WriteLine();

            foreach (var app in visible)
            {
                var size = app.EstimatedSizeBytes > 0 ? Format(app.EstimatedSizeBytes) : S("Cli.Dash");
                var scope = app.Scope == InstallScope.User ? " " + S("Cli.ForYou") : string.Empty;
                var runtime = app.LooksLikeRuntime ? "  " + S("Cli.NeededByOthers") : string.Empty;
                var cannot = app.CanUninstall ? string.Empty : "  " + S("Cli.NoUninstallCommand");

                Console.WriteLine($"  {Trim(app.DisplayName, 44),-44} {Trim(app.Version, 14),-14} {size,10}{scope}{runtime}{cannot}");
            }

            if (!showRuntimes && runtimes.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine(S("Cli.RuntimesHidden", runtimes.Count));
                Console.WriteLine(S("Cli.RuntimesWhy"));
            }

            Console.WriteLine();
            Console.WriteLine(S("Cli.SizeSelfReported"));

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
                        Console.WriteLine(S("Cli.NoPath"));
                        return 1;
                    }

                    var result = manager.Enable(executable, ScheduleFrequency.Weekly, atLogon: false);
                    Console.WriteLine(result.Message);

                    if (result.Success)
                    {
                        Console.WriteLine(S("Cli.ScheduleSafeOnly1"));
                        Console.WriteLine(S("Cli.ScheduleSafeOnly2"));
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
                        Console.WriteLine(S("Cli.ScheduleOff"));
                        Console.WriteLine(S("Cli.ScheduleHowOn"));
                        return 0;
                    }

                    Console.WriteLine(S("Cli.ScheduleOn", state.Description));

                    if (state.NextRun is { } next)
                    {
                        Console.WriteLine(S("Cli.ScheduleNext", next.ToString("dd.MM.yyyy HH:mm")));
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
                await WriteReportAsync(reportPath, null, S("Cli.PlanNotFound"));
                return 2;
            }

            if (!IsInsideUserTemp(planPath))
            {
                // Задание для процесса с правами администратора не может лежать там,
                // где его способен подменить другой пользователь.
                await WriteReportAsync(reportPath, null, S("Cli.PlanOutsideTemp"));
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
                await WriteReportAsync(reportPath, null, S("Cli.PlanUnreadable", ex.Message));
                return 4;
            }

            if (plan is null || plan.TotalCount == 0)
            {
                await WriteReportAsync(reportPath, null, S("Cli.PlanEmpty"));
                return 5;
            }

            // Ветки реестра карантин не покрывает — их возвращает только выгрузка в файл.
            var backup = await new RegistryBackup().SaveAsync(plan);

            // Последний рубеж для операций, меняющих устройство системы. Права здесь
            // уже есть: этот процесс для того и поднят.
            if (RestorePoint.IsWorthIt(plan))
            {
                var point = new RestorePoint().Create(S("Cli.BeforeVacate"));

                Console.WriteLine(point.Status == RestorePointStatus.Created
                    ? S("Cli.RestorePointMade")
                    : S("Cli.RestorePointFailed", point.Message));
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

            await WriteReportAsync(reportPath, report, null, backup?.Path);

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
            => WriteReportAsync(reportPath, null, S("Cli.PlanFailure", exception.Message));

        private static async Task WriteReportAsync(
            string? reportPath,
            ExecutionReport? report,
            string? error,
            string? registryBackupPath = null)
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
                    Error: error,
                    RegistryBackupPath: registryBackupPath);

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
                var Message = S("Cli.IntegrityNeedsRights");

                Console.WriteLine(Message);
                await WriteIntegrityReportAsync(reportPath, IntegrityStatus.NeedsElevation, Message);

                return 2;
            }

            Console.WriteLine(S("Cli.IntegrityHeader"));
            Console.WriteLine(S("Cli.IntegrityHow1"));
            Console.WriteLine(S("Cli.IntegrityHow2"));
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

        /// <summary>
        /// Слежение за установкой: снимок до, сравнение после.
        /// </summary>
        /// <remarks>
        /// Снимок «до» надо успеть сделать ДО запуска установщика. Если программа уже
        /// установлена, сравнивать не с чем, и разница покажет случайный мусор,
        /// накопившийся за это время, — поэтому команда говорит об этом прямо.
        /// </remarks>
        public static int Watch(string[] arguments)
        {
            var watcher = new InstallWatcher();
            var action = arguments.FirstOrDefault()?.ToLowerInvariant();

            if (action == "list")
            {
                var saved = watcher.List();

                if (saved.Count == 0)
                {
                    Console.WriteLine(S("Cli.NoWatches"));
                    return 0;
                }

                Console.WriteLine(S("Cli.OpenWatches"));
                saved.ToList().ForEach(s => Console.WriteLine($"  {s}"));

                return 0;
            }

            if (action == "diff")
            {
                return Diff(watcher, arguments.Skip(1).FirstOrDefault());
            }

            if (action == "forget")
            {
                var name = arguments.Skip(1).FirstOrDefault();

                if (string.IsNullOrWhiteSpace(name))
                {
                    Console.WriteLine(S("Cli.WatchNameForget"));
                    return 2;
                }

                watcher.Forget(name);
                Console.WriteLine(S("Cli.WatchClosed", name));

                return 0;
            }

            var label = arguments.FirstOrDefault(a => !a.StartsWith("--"));

            if (string.IsNullOrWhiteSpace(label))
            {
                Console.WriteLine(S("Cli.WatchName"));
                Console.WriteLine(S("Cli.WatchThen"));
                return 2;
            }

            Console.WriteLine(S("Cli.WatchTaking"));

            var path = watcher.Save(watcher.Capture(), label);

            Console.WriteLine(S("Cli.WatchSaved", path));
            Console.WriteLine();
            Console.WriteLine(S("Cli.WatchNowInstall"));
            Console.WriteLine($"  vacate watch diff {label}");

            return 0;
        }

        private static int Diff(InstallWatcher watcher, string? label)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                Console.WriteLine(S("Cli.WatchDiffName"));
                return 2;
            }

            var before = watcher.Load(label);

            if (before is null)
            {
                Console.WriteLine(S("Cli.WatchNotFound", label));
                return 1;
            }

            Console.WriteLine(S("Cli.WatchComparing"));

            var difference = watcher.Compare(before, watcher.Capture());

            Console.WriteLine();
            Console.WriteLine(S("Cli.WatchSince", before.TakenAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm")));
            Console.WriteLine();

            if (difference.IsEmpty)
            {
                Console.WriteLine(S("Cli.WatchNothingNew"));
                return 0;
            }

            if (difference.NewApps.Count > 0)
            {
                Console.WriteLine(S("Cli.WatchNewApps", difference.NewApps.Count));
                difference.NewApps.ToList().ForEach(a => Console.WriteLine($"  {a}"));
                Console.WriteLine();
            }

            if (difference.NewDirectories.Count > 0)
            {
                Console.WriteLine(S("Cli.WatchNewDirs", difference.NewDirectories.Count));
                difference.NewDirectories.ToList().ForEach(d => Console.WriteLine($"  {d}"));
                Console.WriteLine();
            }

            if (difference.NewRegistryKeys.Count > 0)
            {
                Console.WriteLine(S("Cli.WatchNewKeys", difference.NewRegistryKeys.Count));
                difference.NewRegistryKeys.ToList().ForEach(k => Console.WriteLine($"  {k}"));
                Console.WriteLine();
            }

            // Между снимками работает не только установщик: система обновляется,
            // браузер пишет кэш, антивирус обновляет базы. Молчать об этом нельзя.
            Console.WriteLine(S("Cli.WatchCaveat1"));
            Console.WriteLine(S("Cli.WatchCaveat2"));
            Console.WriteLine();
            Console.WriteLine(S("Cli.WatchFinish", label));

            return 0;
        }

        /// <summary>Создать точку восстановления по прямой просьбе.</summary>
        public static int CreateRestorePoint()
        {
            var result = new RestorePoint().Create(S("Cli.RestoreManual"));

            Console.WriteLine(result.Message);

            if (result.Status == RestorePointStatus.Disabled)
            {
                Console.WriteLine();
                Console.WriteLine(S("Cli.ProtectionHowOn1"));
                Console.WriteLine(S("Cli.ProtectionHowOn2"));
            }

            return result.Status == RestorePointStatus.Created ? 0 : 1;
        }

        public static int Health()
        {
            var disks = new DiskHealthReader().Read();

            if (disks.Count == 0)
            {
                Console.WriteLine(S("Cli.NoDiskInfo"));
                Console.WriteLine(S("Cli.NoDiskInfoWhy"));
                return 1;
            }

            foreach (var disk in disks)
            {
                Console.WriteLine($"{disk.Model}  ({disk.MediaType}, {Format(disk.SizeBytes)})");
                Console.WriteLine(S("Cli.DiskState", DescribeHealth(disk.Health)));

                if (disk.TemperatureCelsius is { } temperature)
                {
                    Console.WriteLine(S("Cli.DiskTemp", temperature));
                }

                if (disk.WearPercent is { } wear)
                {
                    Console.WriteLine(S("Cli.DiskWear", wear));
                }

                if (disk.PowerOnHours is { } hours)
                {
                    Console.WriteLine(S("Cli.DiskHours", hours, (hours / 24 / 365.0).ToString("0.#")));
                }

                if (disk.ReadErrorsTotal is { } errors && errors > 0)
                {
                    Console.WriteLine(S("Cli.DiskErrors", errors));
                }

                // Молчание диска — это не «всё хорошо», и говорить об этом надо прямо.
                if (disk.Unavailable.Count > 0)
                {
                    Console.WriteLine(S("Cli.DiskSilentPrefix") + string.Join(", ", disk.Unavailable));
                }

                if (disk.NeedsAttention)
                {
                    Console.WriteLine(S("Cli.DiskAttention"));
                }

                Console.WriteLine();
            }

            Console.WriteLine(S("Cli.DiskNote1"));
            Console.WriteLine(S("Cli.DiskNote2"));

            return 0;
        }

        private static string DescribeHealth(DiskHealthStatus status) => status switch
        {
            DiskHealthStatus.Healthy => S("Cli.HealthOk"),
            DiskHealthStatus.Warning => S("Cli.HealthWarn"),
            DiskHealthStatus.Unhealthy => S("Cli.HealthBad"),
            _ => S("Cli.HealthUnknown"),
        };

        public static int Disk(string? root)
        {
            root ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (!Directory.Exists(root))
            {
                Console.WriteLine(S("Cli.FolderNotFound", root));
                return 1;
            }

            Console.WriteLine(S("Cli.Analysing", root));
            Console.WriteLine(S("Cli.AnalysingNote"));
            Console.WriteLine();

            var result = new DiskAnalyzer(new QuarantinePathCheck()).Analyze(root);

            Console.WriteLine(S("Cli.Scanned", result.TotalFilesScanned, Format(result.TotalBytesScanned)));
            Console.WriteLine();

            Console.WriteLine(S("Cli.WhereSpace"));
            foreach (var category in result.ByCategory.Take(8))
            {
                Console.WriteLine($"  {category.Category,-30} {Format(category.TotalBytes),12}   {category.FileCount} {S("Cli.Items")}");
            }

            Console.WriteLine();
            Console.WriteLine(S("Cli.BiggestFolders"));
            foreach (var directory in result.LargestDirectories.Take(8))
            {
                Console.WriteLine($"  {Trim(Path.GetFileName(directory.Category), 40),-40} {Format(directory.TotalBytes),12}");
            }

            Console.WriteLine();
            Console.WriteLine(S("Cli.BiggestFiles"));
            foreach (var file in result.LargestFiles.Take(8))
            {
                Console.WriteLine($"  {Trim(Path.GetFileName(file.Path), 46),-46} {Format(file.SizeOnDiskBytes),12}");
            }

            if (result.Duplicates.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine(S("Cli.Duplicates", Format(result.RecoverableFromDuplicates)));

                foreach (var group in result.Duplicates.Take(5))
                {
                    Console.WriteLine(S("Cli.DuplicateGroup", group.Files.Count, Format(group.FileSizeBytes)));

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
                Console.WriteLine(S("Cli.NotCounted"));
                result.SkipNotes.ToList().ForEach(n => Console.WriteLine($"  · {n}"));
            }

            return 0;
        }

        public static int Extensions()
        {
            var extensions = new BrowserExtensionScanner().Scan();

            if (extensions.Count == 0)
            {
                Console.WriteLine(S("Cli.NoExtensions"));
                return 0;
            }

            Console.WriteLine(S("Cli.ExtensionsCount", extensions.Count));
            Console.WriteLine();

            // Сначала те, кто просит больше всего прав: именно их стоит пересмотреть.
            foreach (var extension in extensions)
            {
                var size = extension.SizeBytes > 0 ? Format(extension.SizeBytes) : string.Empty;
                var marker = extension.ReadsAllSites ? S("Cli.ReadsAllSites") : string.Empty;

                Console.WriteLine($"  {Trim(extension.Name, 40),-40} {extension.Browser,-16} {size,10}{marker}");

                if (extension.ProfileName != "Default")
                {
                    Console.WriteLine(S("Cli.Profile", extension.ProfileName));
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
                Console.WriteLine(S("Cli.DangerousCount", dangerous));
                Console.WriteLine(S("Cli.DangerousNote"));
            }

            Console.WriteLine(S("Cli.ExtensionsHow1"));
            Console.WriteLine(S("Cli.ExtensionsHow2"));

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
                Console.WriteLine(S("Cli.StartupIdNeeded"));
                Console.WriteLine(S("Cli.StartupIdWhere"));
                return 2;
            }

            var entry = new StartupScanner().Scan()
                .FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

            if (entry is null)
            {
                Console.WriteLine(S("Cli.EntryNotFound", id));
                return 1;
            }

            if (entry.IsEnabled == enable)
            {
                Console.WriteLine(S(enable ? "Cli.AlreadyOn" : "Cli.AlreadyOff", entry.Name));
                return 0;
            }

            var outcome = new StartupToggle().Set(entry, enable);

            if (!outcome.Success)
            {
                Console.WriteLine(outcome.Message ?? S("Cli.ToggleFailed"));

                if (StartupToggle.RequiresElevation(entry) && !SystemIntegrityChecker.IsElevated())
                {
                    Console.WriteLine(S("Cli.NeedsAdminEntry"));
                }

                return 1;
            }

            Console.WriteLine(S(enable ? "Cli.TurnedOn" : "Cli.TurnedOff", entry.Name));

            if (entry.Source == StartupSource.Service && !enable)
            {
                // Разница существенная, и человек должен о ней знать: иначе решит,
                // что отключение не сработало, увидев службу работающей.
                Console.WriteLine(S("Cli.ServiceManual1"));
                Console.WriteLine(S("Cli.ServiceManual2"));
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

            Console.WriteLine(S("Cli.StartupCount", visible.Count));
            Console.WriteLine();

            foreach (var group in visible.GroupBy(e => e.Source))
            {
                Console.WriteLine($"{DescribeSource(group.Key)}:");

                foreach (var entry in group)
                {
                    var state = S(entry.IsEnabled ? "Cli.On" : "Cli.Off");
                    var scope = S(entry.Scope == InstallScope.User ? "Cli.ScopeYou" : "Cli.ScopeAll");
                    var locked = entry.Control == StartupControl.ViewOnly ? S("Cli.ViewOnly") : string.Empty;

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
                Console.WriteLine(S("Cli.ServicesCount", services.Count, locked));
                Console.WriteLine(S("Cli.ShowAll"));
            }

            return 0;
        }

        private static string DescribeSource(StartupSource source) => source switch
        {
            StartupSource.RunKey => S("Cli.SourceRun"),
            StartupSource.StartupFolder => S("Cli.SourceFolder"),
            StartupSource.ScheduledTask => S("Cli.SourceTask"),
            _ => S("Cli.SourceService"),
        };

        public static int Leftovers(string? query)
        {
            var app = ResolveApp(query, S("Cli.UsageLeftovers"));

            if (app is null)
            {
                return 2;
            }

            Console.WriteLine(S("Cli.Traces", app.DisplayName));

            var found = new LeftoverScanner().Scan(app);

            if (found.Count == 0)
            {
                Console.WriteLine(S("Cli.NoTracesFound"));
                return 0;
            }

            PrintLeftovers(found);

            Console.WriteLine(S("Cli.ShowOnly"));

            // Имя в кавычках: почти все названия многословны, и подсказка без кавычек
            // отправила бы человека в ошибку «подходит несколько программ».
            Console.WriteLine(S("Cli.HowToRemove", app.DisplayName));
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
            var app = ResolveApp(query, S("Cli.UsageUninstall"));

            if (app is null)
            {
                return 2;
            }

            Console.WriteLine(S("Cli.Program", app.DisplayName));
            Console.WriteLine(S("Cli.Publisher", app.Publisher ?? S("Cli.NotStated")));
            Console.WriteLine(S("Cli.Version", app.Version ?? S("Cli.VersionNotStated")));
            Console.WriteLine(S("Cli.Directory", app.InstallLocation ?? S("Cli.NotStated")));
            Console.WriteLine();

            if (app.LooksLikeRuntime)
            {
                // Не запрет, а честное предупреждение: удалять компоненты иногда нужно,
                // но человек должен знать, что ломает не одну программу.
                Console.WriteLine(S("Cli.RuntimeWarning"));
                Console.WriteLine(S("Cli.RuntimeWarning2"));
                Console.WriteLine();
            }

            // Пока штатный деинсталлятор на месте, идём через него: он знает про свою
            // программу больше, чем можем узнать мы по косвенным признакам.
            if (ForcedUninstall.IsApplicable(app))
            {
                Console.WriteLine(S(app.CanUninstall ? "Cli.ForcedIntro1" : "Cli.ForcedIntro2"));
                Console.WriteLine(S("Cli.ForcedIntro3"));
                Console.WriteLine();

                if (!Confirm(S("Cli.ForcedConfirm"), assumeYes))
                {
                    Console.WriteLine(S("Cli.Cancelled"));
                    return 0;
                }

                return await CleanLeftoversAsync(app, assumeYes, forced: true);
            }

            Console.WriteLine(S("Cli.UninstallerIntro1"));
            Console.WriteLine(S("Cli.UninstallerIntro2"));
            Console.WriteLine();

            if (!Confirm(S("Cli.StartRemoval"), assumeYes))
            {
                Console.WriteLine(S("Cli.Cancelled"));
                return 0;
            }

            Console.WriteLine(S("Cli.Waiting"));

            var outcome = await new UninstallRunner()
                .RunAsync(app, silent, TimeSpan.FromMinutes(30), CancellationToken.None);

            Console.WriteLine();

            if (outcome.Message is not null)
            {
                Console.WriteLine(outcome.Message);
            }

            if (outcome.Status is UninstallStatus.Failed or UninstallStatus.TimedOut)
            {
                Console.WriteLine(S("Cli.NoTraceSearch"));
                return 1;
            }

            if (outcome.Status == UninstallStatus.Completed && outcome.Message is null)
            {
                Console.WriteLine(S("Cli.UninstallerDone"));
            }

            Console.WriteLine();
            return await CleanLeftoversAsync(app, assumeYes);
        }

        /// <summary>Найти и предложить к удалению то, что деинсталлятор не убрал.</summary>
        /// <param name="forced">
        /// Деинсталлятора не было вовсе: запись из списка установленного придётся
        /// убрать самим, и сделать это надо последним действием.
        /// </param>
        private static async Task<int> CleanLeftoversAsync(InstalledApp app, bool assumeYes, bool forced = false)
        {
            Console.WriteLine(S("Cli.Searching"));

            var found = new LeftoverScanner().Scan(app);

            if (found.Count == 0)
            {
                Console.WriteLine(S("Cli.NoFiles"));

                return forced ? RemoveRegistration(app) : 0;
            }

            PrintLeftovers(found);

            // Уровень «возможно» не отмечается никогда автоматически: за ним стоит
            // одно совпадение части имени, и ошибка стоит чужого каталога с данными.
            var proposed = found.Where(f => f.Confidence != LeftoverConfidence.Possible).ToList();
            var uncertain = found.Count - proposed.Count;

            if (proposed.Count == 0)
            {
                Console.WriteLine(S("Cli.NothingProposed"));
                Console.WriteLine(S("Cli.CheckYourself"));
                return 0;
            }

            var size = proposed.Sum(p => p.SizeOnDiskBytes);

            Console.WriteLine(S("Cli.Proposed", proposed.Count, Format(size)));

            if (uncertain > 0)
            {
                Console.WriteLine(S("Cli.UncertainLeft", uncertain));
            }

            Console.WriteLine(S("Cli.QuarantineNote"));
            Console.WriteLine();

            if (!Confirm(S("Cli.RemoveProposed"), assumeYes))
            {
                Console.WriteLine(S("Cli.Cancelled"));
                return 0;
            }

            var plan = new LeftoverPlanBuilder().Build(app, proposed);

            if (plan.TotalCount == 0)
            {
                Console.WriteLine(S("Cli.NothingToRemove"));
                return 0;
            }

            // Ветки реестра карантин не покрывает: копия делается до удаления,
            // после него сохранять уже нечего.
            var backup = await new RegistryBackup().SaveAsync(plan);

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
                Console.WriteLine(S("Cli.UndoDirs", report.SessionId));
            }

            if (backup?.Path is not null)
            {
                Console.WriteLine(S("Cli.BackupAt", backup.Path));
                Console.WriteLine(S("Cli.BackupHowTo"));
            }

            if (backup is { Failed.Count: > 0 })
            {
                // Молчаливый пропуск читался бы как «всё сохранено».
                Console.WriteLine(S("Cli.BackupFailed", string.Join(", ", backup.Failed)));
            }

            // Запись из списка убирается ПОСЛЕДНЕЙ: убери её первой — и при сорвавшемся
            // удалении файлов программа исчезла бы из списка, оставшись на диске.
            if (forced)
            {
                Console.WriteLine();
                RemoveRegistration(app);
            }

            return report.Failed > 0 ? 1 : 0;
        }

        /// <summary>Убрать запись программы из списка установленного.</summary>
        private static int RemoveRegistration(InstalledApp app)
        {
            var outcome = new ForcedUninstall().RemoveRegistration(app);

            Console.WriteLine(outcome.Message);

            if (!outcome.Success)
            {
                Console.WriteLine(S("Cli.StillListed"));
            }

            return outcome.Success ? 0 : 1;
        }

        /// <summary>Найти единственную программу по части названия.</summary>
        private static InstalledApp? ResolveApp(string? query, string usage)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                Console.WriteLine(S("Cli.SpecifyProgram", usage));
                return null;
            }

            var matches = new InstalledAppsScanner().Scan()
                .Where(a => a.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

            switch (matches.Count)
            {
                case 0:
                    Console.WriteLine(S("Cli.NotFound", query));
                    return null;

                case 1:
                    return matches[0];

                default:
                    // Угадывать нельзя: удалили бы не то, что имел в виду человек.
                    Console.WriteLine(S("Cli.Ambiguous"));
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
                    Console.WriteLine(S("Cli.Why", string.Join("; ", item.Evidence)));
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
                Console.WriteLine(S("Cli.ConfirmYes", question));
                return true;
            }

            if (Console.IsInputRedirected)
            {
                // Спросить некого: команду запустили из сценария без ключа согласия.
                Console.WriteLine(S("Cli.NoInput", question));
                return false;
            }

            Console.Write(S("Cli.ConfirmPrompt", question));
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();

            // Оба языка принимаются независимо от выбранного: человек, переключивший
            // интерфейс, по привычке набирает то, к чему привык.
            return answer is "да" or "yes" or "y" or "д";
        }

        private static string DescribeConfidence(LeftoverConfidence confidence) => confidence switch
        {
            LeftoverConfidence.Certain => S("Cli.ConfCertain"),
            LeftoverConfidence.Likely => S("Cli.ConfLikely"),
            _ => S("Cli.ConfPossible"),
        };

        private static string Trim(string? value, int length)
        {
            if (string.IsNullOrEmpty(value))
            {
                return S("Cli.Dash");
            }

            return value.Length <= length ? value : value[..(length - 1)] + S("Cli.Ellipsis");
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
                Console.WriteLine(S("Cli.NothingToDo"));
                await WriteReportAsync(reportPath, null, null);
                return 0;
            }

            Console.WriteLine(dryRun
                ? S("Cli.DryRun", plan.TotalCount, Format(plan.TotalSizeOnDiskBytes))
                : S("Cli.Cleaning", plan.TotalCount, Format(plan.TotalSizeOnDiskBytes)));
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
                Console.WriteLine(S("Cli.NoSessions"));
                return 0;
            }

            foreach (var session in sessions)
            {
                var restorable = session.HasRestorableItems ? S("Cli.Restorable") : string.Empty;

                Console.WriteLine(S(
                    "Cli.SessionLine",
                    session.SessionId,
                    session.StartedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
                    Format(session.ActuallyFreedBytes),
                    session.ItemCount,
                    restorable));
            }

            return 0;
        }

        public static async Task<int> UndoAsync(string? sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                Console.WriteLine(S("Cli.SpecifySession"));
                return 2;
            }

            var journal = new JsonlOperationJournal(Path.Combine(DataDirectory, "journal"));
            var quarantine = new FileSystemQuarantine();

            var undoable = await journal.GetUndoableAsync(sessionId, CancellationToken.None);

            if (undoable.Count == 0)
            {
                Console.WriteLine(S("Cli.NothingUndoable"));
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
                    Console.WriteLine(S("Cli.NotRestored", entry.OriginalPath));
                }
            }

            Console.WriteLine(S("Cli.UndoResult", restored, failed));
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
                Console.WriteLine(S("Cli.WouldProcess", report.Succeeded, Format(report.ClaimedBytes)));
                Console.WriteLine(S("Cli.NothingChanged"));
            }
            else
            {
                // Две цифры рядом — суть честного счётчика.
                Console.WriteLine(S("Cli.Removed", report.Succeeded, Format(report.ClaimedBytes)));
                Console.WriteLine(S("Cli.ActuallyFreed", Format(report.ActuallyFreedBytes)));
            }

            if (report.Skipped > 0)
            {
                Console.WriteLine(S("Cli.Skipped", report.Skipped));
            }

            if (report.Failed > 0)
            {
                Console.WriteLine(S("Cli.Failed", report.Failed));
            }

            if (report.Denied > 0)
            {
                Console.WriteLine(S("Cli.Denied", report.Denied));
            }

            if (report.Discrepancies.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine(S("Cli.WhereDifference"));

                foreach (var reason in report.Discrepancies)
                {
                    Console.WriteLine($"  {Explain(reason.Kind),-46} {Format(reason.Bytes),12}" +
                                      (reason.Detail is null ? string.Empty : $"  ({reason.Detail})"));
                }
            }

            if (report.Cancelled)
            {
                Console.WriteLine();
                Console.WriteLine(S("Cli.CancelledNote"));
            }
        }

        private static string Explain(DiscrepancyKind kind) => kind switch
        {
            DiscrepancyKind.HeldByProcess => S("Cli.WhyHeld"),
            DiscrepancyKind.NotDeleted => S("Cli.WhyNotDeleted"),
            DiscrepancyKind.HardLinked => S("Cli.WhyHardLinked"),
            DiscrepancyKind.CompressedOrSparse => S("Cli.WhyCompressed"),
            DiscrepancyKind.InQuarantine => S("Cli.WhyQuarantine"),
            DiscrepancyKind.InRecycleBin => S("Cli.WhyRecycleBin"),
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
                "Clean.Temp.User" => S("Cli.CatTempUser"),
                "Clean.Temp.System" => S("Cli.CatTempSystem"),
                "Clean.Logs.Windows" => S("Cli.CatLogs"),
                "Clean.Cache.Browsers" => S("Cli.CatBrowsers"),
                "Clean.Crash.Reports" => S("Cli.CatCrash"),
                "Clean.Cache.Delivery" => S("Cli.CatDelivery"),
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
            // Сокращения единиц переводятся: «КБ» в английском выводе выглядит
            // так же чужеродно, как «KB» в русском.
            string[] units = Strings.IsEnglish
                ? ["B", "KB", "MB", "GB", "TB"]
                : ["Б", "КБ", "МБ", "ГБ", "ТБ"];

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
