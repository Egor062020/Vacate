using System.Runtime.InteropServices;
using Vacate.Abstractions.Model;
using Vacate.Core.Safety;

namespace Vacate.Platform.Windows.Files;

/// <summary>
/// Сканер временных файлов: строит план очистки, ничего не меняя.
/// </summary>
/// <remarks>
/// Правила, заложенные в обход, продиктованы разбором проекта:
///
///   - файлы моложе суток не трогаются: их может прямо сейчас использовать работающая
///     программа или идущая установка;
///   - точки повторной обработки не проходятся: соединение каталогов увело бы обход
///     в системные папки, а поиск дубликатов показал бы один файл как две копии;
///   - собственные каталоги программы и карантин исключаются: без этого очистка
///     сломала бы саму программу посреди работы;
///   - размер берётся как занимаемое место на диске, а не логический: для сжатых
///     и разреженных файлов это разные величины, и честный счётчик обязан считать первую.
/// </remarks>
public sealed class TempFilesScanner(PathPolicy policy, IQuarantinePathCheck? quarantine = null)
{
    /// <summary>Возраст, младше которого файлы не трогаются.</summary>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromDays(1);

    /// <summary>Построить план очистки временных каталогов.</summary>
    public MutationPlan Scan(IEnumerable<TempLocation> locations, CancellationToken ct)
    {
        var groups = new List<OperationGroup>();

        foreach (var location in locations)
        {
            ct.ThrowIfCancellationRequested();

            var operations = new List<PlannedOperation>();
            long totalSize = 0;
            var index = 0;

            foreach (var root in location.Paths)
            {
                ct.ThrowIfCancellationRequested();

                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var file in EnumerateFiles(root, ct))
                {
                    var target = Describe(file);

                    if (target is null)
                    {
                        continue;
                    }

                    operations.Add(new DeleteFileOperation
                    {
                        Id = $"{location.Id}-{index++}",
                        GroupId = location.Id,
                        DeclaredRisk = location.Risk,
                        Consequence = LocalizedText.FromResource(location.ConsequenceKey),
                        Target = target,

                        // Временные файлы и кэши удаляются безвозвратно, и пользователю
                        // это говорится прямо. Класть их в карантин бессмысленно: они
                        // создаются заново, а место при этом не освободилось бы.
                        Disposition = DeleteDisposition.Permanent,
                    });

                    totalSize += target.SizeOnDiskBytes;
                }
            }

            if (operations.Count > 0)
            {
                groups.Add(new OperationGroup
                {
                    GroupId = location.Id,
                    Title = LocalizedText.FromResource(location.TitleKey),
                    RootPath = location.RootPath,
                    Operations = operations,
                    SizeOnDiskBytes = totalSize,
                });
            }
        }

        return new MutationPlan
        {
            PlanId = Guid.NewGuid().ToString("N"),
            Origin = "cleaner.temp",
            Groups = groups,
        };
    }

    private IEnumerable<string> EnumerateFiles(string root, CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = pending.Pop();

            if (quarantine?.IsQuarantinePath(current) == true)
            {
                continue;
            }

            string[] subdirectories;

            try
            {
                subdirectories = Directory.GetDirectories(current);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                // Каталог мог исчезнуть или быть недоступен. Это обычное состояние
                // временных папок, а не повод прерывать сканирование.
                continue;
            }

            foreach (var directory in subdirectories)
            {
                try
                {
                    var attributes = File.GetAttributes(directory);

                    // За точки повторной обработки не идём никогда: обход ушёл бы
                    // по ссылке в чужие, в том числе системные, каталоги.
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    if (!policy.Evaluate(directory).IsAllowed)
                    {
                        continue;
                    }

                    pending.Push(directory);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or IOException)
                {
                    // Пропускаем недоступное молча — отчёт покажет итог по группе.
                }
            }

            string[] files;

            try
            {
                files = Directory.GetFiles(current);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }

    private FileTarget? Describe(string path)
    {
        try
        {
            var info = new FileInfo(path);

            if (!info.Exists)
            {
                return null;
            }

            // Свежий файл может принадлежать работающей программе или идущей установке.
            // Удаление такого файла ломает чужую работу и выглядит как поломка системы.
            if (DateTime.UtcNow - info.LastWriteTimeUtc < MinimumAge)
            {
                return null;
            }

            var attributes = info.Attributes;

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return null;
            }

            if (!policy.Evaluate(path).IsAllowed)
            {
                return null;
            }

            var traits = FileTraits.None;

            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                traits |= FileTraits.ReadOnly;
            }

            if (attributes.HasFlag(FileAttributes.Compressed) || attributes.HasFlag(FileAttributes.SparseFile))
            {
                traits |= FileTraits.CompressedOrSparse;
            }

            // Облачные заглушки: содержимое физически не скачано. Такие файлы нельзя
            // открывать — система начнёт их загружать, израсходовав трафик пользователя.
            const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
            const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

            if (attributes.HasFlag(RecallOnOpen) || attributes.HasFlag(RecallOnDataAccess) || attributes.HasFlag(FileAttributes.Offline))
            {
                traits |= FileTraits.CloudPlaceholder;
            }

            return new FileTarget(path, IsDirectory: false, SizeOnDiskBytes: GetSizeOnDisk(info), Traits: traits);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Занимаемое место на диске.
    /// </summary>
    /// <remarks>
    /// Для сжатых и разреженных файлов оно меньше логического размера, и разница бывает
    /// кратной. Счётчик, складывающий логические размеры, обещал бы освободить больше,
    /// чем освободится, — то есть делал бы ровно то, за что продукт критикует конкурентов.
    /// </remarks>
    private static long GetSizeOnDisk(FileInfo info)
    {
        var low = GetCompressedFileSizeW(info.FullName, out var high);

        if (low == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0)
        {
            return info.Length;
        }

        return ((long)high << 32) | low;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);
}

/// <summary>
/// Категория очистки: что чистим и чем это грозит.
/// </summary>
/// <param name="Id">Идентификатор группы.</param>
/// <param name="TitleKey">Ключ названия для показа пользователю.</param>
/// <param name="Paths">
/// Каталоги категории. Их несколько: у браузера столько же кэшей, сколько профилей,
/// и показывать человеку восемь строк «кэш Chrome» вместо одной значит заставить
/// его разбираться в устройстве чужой программы.
/// </param>
/// <param name="Risk">Заявленный уровень риска.</param>
/// <param name="ConsequenceKey">Что именно человек потеряет. Показывается до нажатия.</param>
public sealed record TempLocation(
    string Id,
    string TitleKey,
    IReadOnlyList<string> Paths,
    RiskLevel Risk = RiskLevel.Green,
    string ConsequenceKey = "Clean.Temp.Consequence")
{
    /// <summary>Категория из одного каталога.</summary>
    public TempLocation(string id, string titleKey, string path, RiskLevel risk = RiskLevel.Green)
        : this(id, titleKey, [path], risk)
    {
    }

    /// <summary>Общий корень, если каталог один. Нужен охране для дешёвой проверки.</summary>
    public string? RootPath => Paths.Count == 1 ? Paths[0] : null;

    /// <summary>
    /// Все категории очистки.
    /// </summary>
    /// <remarks>
    /// Возвращается полный список, а выбор делает человек. Категории с последствиями
    /// объявлены жёлтыми: удаление кэша браузера ничего не ломает, но первое открытие
    /// сайтов заметно замедлится, и об этом честнее сказать заранее, чем выслушивать
    /// потом «после вашей чистки интернет стал медленнее».
    /// </remarks>
    public static IReadOnlyList<TempLocation> Standard()
    {
        var locations = new List<TempLocation>
        {
            new("temp.user", "Clean.Temp.User", System.IO.Path.GetTempPath()),
        };

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        if (!string.IsNullOrEmpty(windows))
        {
            locations.Add(new TempLocation("temp.system", "Clean.Temp.System", System.IO.Path.Combine(windows, "Temp")));
            locations.Add(new TempLocation("logs.windows", "Clean.Logs.Windows", System.IO.Path.Combine(windows, "Logs")));
        }

        AddIfAny(locations, "cache.browsers", "Clean.Cache.Browsers", BrowserCachePaths(local, roaming),
            RiskLevel.Yellow, "Clean.Cache.Browsers.Consequence");

        AddIfAny(locations, "crash.reports", "Clean.Crash.Reports",
        [
            System.IO.Path.Combine(local, "CrashDumps"),
            System.IO.Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportArchive"),
            System.IO.Path.Combine(programData, "Microsoft", "Windows", "WER", "ReportQueue"),
        ]);

        AddIfAny(locations, "cache.delivery", "Clean.Cache.Delivery",
            [System.IO.Path.Combine(local, "Microsoft", "Windows", "DeliveryOptimization", "Cache")]);

        return locations;
    }

    /// <summary>
    /// Кэши браузеров по всем профилям.
    /// </summary>
    /// <remarks>
    /// Профилей у человека может быть несколько, и называются они не только «Default»:
    /// «Profile 1», «Profile 2» и так далее. Перечисление идёт по факту, а не по догадке.
    /// </remarks>
    private static List<string> BrowserCachePaths(string local, string roaming)
    {
        var found = new List<string>();

        // Браузеры на общем движке держат кэш одинаково: <корень>\<профиль>\Cache.
        var chromiumRoots = new[]
        {
            System.IO.Path.Combine(local, "Google", "Chrome", "User Data"),
            System.IO.Path.Combine(local, "Microsoft", "Edge", "User Data"),
            System.IO.Path.Combine(local, "Yandex", "YandexBrowser", "User Data"),
            System.IO.Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"),
            System.IO.Path.Combine(local, "Vivaldi", "User Data"),
            System.IO.Path.Combine(roaming, "Opera Software", "Opera Stable"),
        };

        foreach (var root in chromiumRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var profile in Directory.GetDirectories(root))
                {
                    foreach (var name in (string[])["Cache", "Code Cache", "GPUCache"])
                    {
                        var path = System.IO.Path.Combine(profile, name);

                        if (Directory.Exists(path))
                        {
                            found.Add(path);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Недоступный каталог просто не попадает в список.
            }
        }

        // Firefox устроен иначе: профили лежат отдельно, а кэш называется cache2.
        var firefox = System.IO.Path.Combine(local, "Mozilla", "Firefox", "Profiles");

        if (Directory.Exists(firefox))
        {
            try
            {
                foreach (var profile in Directory.GetDirectories(firefox))
                {
                    var path = System.IO.Path.Combine(profile, "cache2");

                    if (Directory.Exists(path))
                    {
                        found.Add(path);
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // То же самое.
            }
        }

        return found;
    }

    private static void AddIfAny(
        List<TempLocation> locations,
        string id,
        string titleKey,
        IReadOnlyList<string> candidates,
        RiskLevel risk = RiskLevel.Green,
        string consequenceKey = "Clean.Temp.Consequence")
    {
        var existing = candidates.Where(Directory.Exists).ToList();

        // Категория, для которой на этой машине ничего нет, в списке не показывается:
        // пустая строка с нулём заставляет человека гадать, что он сделал не так.
        if (existing.Count > 0)
        {
            locations.Add(new TempLocation(id, titleKey, existing, risk, consequenceKey));
        }
    }
}

/// <summary>Проверка принадлежности пути карантину. Нужна сканеру, чтобы не находить собственные файлы.</summary>
public interface IQuarantinePathCheck
{
    bool IsQuarantinePath(string path);
}
