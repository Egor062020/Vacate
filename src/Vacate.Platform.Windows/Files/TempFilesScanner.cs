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

            if (!Directory.Exists(location.Path))
            {
                continue;
            }

            var operations = new List<PlannedOperation>();
            long totalSize = 0;
            var index = 0;

            foreach (var file in EnumerateFiles(location.Path, ct))
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
                    Consequence = LocalizedText.FromResource("Clean.Temp.Consequence"),
                    Target = target,

                    // Временные файлы и кэши удаляются безвозвратно, и пользователю
                    // это говорится прямо. Класть их в карантин бессмысленно: они
                    // создаются заново, а место при этом не освободилось бы.
                    Disposition = DeleteDisposition.Permanent,
                });

                totalSize += target.SizeOnDiskBytes;
            }

            if (operations.Count > 0)
            {
                groups.Add(new OperationGroup
                {
                    GroupId = location.Id,
                    Title = LocalizedText.FromResource(location.TitleKey),
                    RootPath = location.Path,
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

/// <summary>Каталог, подлежащий очистке.</summary>
/// <param name="Id">Идентификатор группы.</param>
/// <param name="TitleKey">Ключ названия для показа пользователю.</param>
/// <param name="Path">Путь.</param>
/// <param name="Risk">Заявленный уровень риска.</param>
public sealed record TempLocation(string Id, string TitleKey, string Path, RiskLevel Risk = RiskLevel.Green)
{
    /// <summary>Стандартные временные каталоги.</summary>
    public static IReadOnlyList<TempLocation> Standard()
    {
        var locations = new List<TempLocation>
        {
            new("temp.user", "Clean.Temp.User", System.IO.Path.GetTempPath()),
        };

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        if (!string.IsNullOrEmpty(windows))
        {
            locations.Add(new TempLocation("temp.system", "Clean.Temp.System", System.IO.Path.Combine(windows, "Temp")));
        }

        return locations;
    }
}

/// <summary>Проверка принадлежности пути карантину. Нужна сканеру, чтобы не находить собственные файлы.</summary>
public interface IQuarantinePathCheck
{
    bool IsQuarantinePath(string path);
}
