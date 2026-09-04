using System.Text;
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
        "startup" => Commands.Startup(showAll: args.Contains("--all")),
        "extensions" => Commands.Extensions(),
        "disk" => Commands.Disk(args.Skip(1).FirstOrDefault(a => !a.StartsWith("--"))),
        "clean" => await Commands.CleanAsync(dryRun: args.Contains("--dry-run")),
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
                  vacate leftovers <имя>   найти следы программы, ничего не удаляя
                  vacate startup           что стартует вместе с Windows (--all: и службы)
                  vacate extensions        расширения браузеров и их права
                  vacate disk <папка>      куда делось место: крупные файлы, дубли, виды
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

        public static int Startup(bool showAll)
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
            if (string.IsNullOrWhiteSpace(query))
            {
                Console.WriteLine("Укажите программу: vacate leftovers <часть названия>. Список — vacate apps.");
                return 2;
            }

            var apps = new InstalledAppsScanner().Scan();
            var matches = apps
                .Where(a => a.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                Console.WriteLine($"Программа с названием «{query}» не найдена.");
                return 1;
            }

            if (matches.Count > 1)
            {
                Console.WriteLine("Подходит несколько программ, уточните запрос:");
                matches.ForEach(a => Console.WriteLine($"  {a.DisplayName}"));
                return 2;
            }

            var app = matches[0];
            Console.WriteLine($"Следы программы «{app.DisplayName}»");
            Console.WriteLine();

            var found = new LeftoverScanner().Scan(app);

            if (found.Count == 0)
            {
                Console.WriteLine("Ничего не найдено — программа не оставила заметных следов.");
                return 0;
            }

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

            Console.WriteLine("Ничего не удалено: это только показ. Удаление появится вместе с интерфейсом.");
            return 0;
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

        public static async Task<int> CleanAsync(bool dryRun)
        {
            var (scanner, policy) = BuildScanner();
            var plan = scanner.Scan(TempLocation.Standard(), CancellationToken.None);

            if (plan.TotalCount == 0)
            {
                Console.WriteLine("Чисто, делать нечего.");
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
                [new EmergencyModeGuard(), new ProtectedPathGuard(policy), new RecycleBinOrderGuard(), new VolumeLimitGuard()],
                [new ReparseAndCloudGuard()],
                dryRun);

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            var progress = new Progress<ExecutionProgress>(p =>
                Console.Write($"\r  {p.ProcessedCount}/{p.TotalCount}   {Format(p.FreedSoFarBytes)}   "));

            var report = await executor.ExecuteAsync(plan, progress, cts.Token);

            Console.WriteLine();
            Console.WriteLine();
            PrintReport(report);

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

        private static string Describe(LocalizedText text) => text.ResourceKey switch
        {
            "Clean.Temp.User" => "Временные файлы пользователя",
            "Clean.Temp.System" => "Временные файлы системы",
            _ => text.ResourceKey ?? "—",
        };

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
