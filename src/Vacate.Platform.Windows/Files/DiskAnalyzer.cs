using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using Vacate.Abstractions.Model;

namespace Vacate.Platform.Windows.Files;

/// <summary>
/// Анализ занятого места: крупные файлы, дубликаты, распределение по видам.
/// </summary>
/// <remarks>
/// Поиск дубликатов — самая опасная функция продукта, потому что пользователь по её итогу
/// удаляет то, что считает лишней копией. Три правила, без которых она вредна:
///
///   1. Обход не идёт по соединениям каталогов. Если папка «Документы» перенаправлена
///      на другой диск, один и тот же файл виден по двум путям, и без этого правила
///      он был бы предложен как дубликат самого себя.
///   2. Файл определяется парой «том + идентификатор файла», а не путём. Совпадение
///      этой пары означает один физический файл, а не две копии.
///   3. Облачные заглушки не открываются никогда. Чтение такого файла заставляет систему
///      скачать его целиком: вместо освобождения места программа израсходовала бы
///      трафик и заняла диск.
/// </remarks>
public sealed class DiskAnalyzer(IQuarantinePathCheck? quarantine = null)
{
    /// <summary>Файлы мельче этого в поиске дубликатов не участвуют: выигрыш не окупает работы.</summary>
    private const long MinimumDuplicateSize = 1024 * 1024;

    /// <summary>Размер начального фрагмента для быстрого отсева непохожих файлов.</summary>
    private const int PartialHashLength = 8 * 1024;

    /// <summary>Проанализировать каталог.</summary>
    public DiskAnalysisResult Analyze(string root, int topCount = 20, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var files = new List<ScannedFile>();
        var notes = new List<string>();
        var skippedReparse = 0;
        var skippedCloud = 0;
        var deniedDirectories = 0;

        var directorySizes = new Dictionary<string, (long Bytes, int Count)>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Walk(root, () => skippedReparse++, () => deniedDirectories++, ct))
        {
            if (file.Traits.HasFlag(FileTraits.CloudPlaceholder))
            {
                skippedCloud++;
                continue;
            }

            files.Add(file);

            // Верхний уровень относительно корня — по нему потом видно, что занимает место.
            var top = TopLevelOf(root, file.Path);

            if (top is not null)
            {
                var current = directorySizes.GetValueOrDefault(top);
                directorySizes[top] = (current.Bytes + file.SizeOnDiskBytes, current.Count + 1);
            }
        }

        if (skippedReparse > 0)
        {
            notes.Add($"пропущено соединений и ссылок: {skippedReparse} (обход по ним привёл бы к повторному счёту и ложным дубликатам)");
        }

        if (skippedCloud > 0)
        {
            notes.Add($"пропущено облачных файлов, не скачанных на диск: {skippedCloud} (их чтение вызвало бы загрузку из сети)");
        }

        if (deniedDirectories > 0)
        {
            notes.Add($"недоступных каталогов: {deniedDirectories}");
        }

        return new DiskAnalysisResult
        {
            LargestFiles = files.OrderByDescending(f => f.SizeOnDiskBytes).Take(topCount).ToList(),
            Duplicates = FindDuplicates(files, ct).OrderByDescending(g => g.RecoverableBytes).Take(topCount).ToList(),
            ByCategory = SummarizeByCategory(files),
            LargestDirectories = directorySizes
                .OrderByDescending(kv => kv.Value.Bytes)
                .Take(topCount)
                .Select(kv => new CategoryUsage(kv.Key, kv.Value.Bytes, kv.Value.Count))
                .ToList(),
            TotalFilesScanned = files.Count,
            TotalBytesScanned = files.Sum(f => f.SizeOnDiskBytes),
            SkipNotes = notes,
        };
    }

    private IEnumerable<ScannedFile> Walk(string root, Action onReparse, Action onDenied, CancellationToken ct)
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
                onDenied();
                continue;
            }

            foreach (var directory in subdirectories)
            {
                try
                {
                    if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                    {
                        onReparse();
                        continue;
                    }

                    pending.Push(directory);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or IOException)
                {
                    onDenied();
                }
            }

            string[] entries;

            try
            {
                entries = Directory.GetFiles(current);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                onDenied();
                continue;
            }

            foreach (var path in entries)
            {
                var described = Describe(path);

                if (described is not null)
                {
                    yield return described;
                }
            }
        }
    }

    private static ScannedFile? Describe(string path)
    {
        try
        {
            var info = new FileInfo(path);

            if (!info.Exists)
            {
                return null;
            }

            var attributes = info.Attributes;

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return null;
            }

            var traits = FileTraits.None;

            const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
            const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

            if (attributes.HasFlag(RecallOnOpen) || attributes.HasFlag(RecallOnDataAccess) || attributes.HasFlag(FileAttributes.Offline))
            {
                traits |= FileTraits.CloudPlaceholder;
            }

            if (attributes.HasFlag(FileAttributes.Compressed) || attributes.HasFlag(FileAttributes.SparseFile))
            {
                traits |= FileTraits.CompressedOrSparse;
            }

            // Идентификатор файла здесь НЕ читается намеренно. Он требует открытия
            // дескриптора на каждый файл, а это на порядок дороже чтения атрибутов:
            // замер на живой машине дал 83 файла в секунду вместо тысяч.
            // Идентификатор нужен только кандидатам в дубликаты, и читается он
            // позже — для них одних.
            return new ScannedFile(path, info.Length, info.LastWriteTimeUtc, VolumeSerial: 0, FileId: 0, traits);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Прочитать пару, однозначно определяющую физический файл.
    /// </summary>
    /// <remarks>
    /// Без этого поиск дубликатов принял бы жёсткие ссылки и файлы, видимые через
    /// соединение каталогов, за разные копии, и удаление «лишней» уничтожило бы
    /// единственный экземпляр данных.
    /// </remarks>
    private static (ulong VolumeSerial, ulong FileId, int LinkCount) ReadIdentity(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            if (GetFileInformationByHandle(handle, out var info))
            {
                var fileId = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
                var serial = (ulong)info.VolumeSerialNumber;

                return (serial, fileId, (int)info.NumberOfLinks);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Занятый файл идентифицировать не удалось — считаем его уникальным,
            // чтобы случайно не объявить дубликатом.
        }

        return (0, 0, 1);
    }

    private List<DuplicateGroup> FindDuplicates(List<ScannedFile> files, CancellationToken ct)
    {
        var groups = new List<DuplicateGroup>();

        // Шаг 1: по размеру. Файлы разного размера заведомо различны,
        // и это отсекает подавляющее большинство без единого чтения с диска.
        var bySize = files
            .Where(f => f.SizeOnDiskBytes >= MinimumDuplicateSize)
            .GroupBy(f => f.SizeOnDiskBytes)
            .Where(g => g.Count() > 1);

        foreach (var sizeGroup in bySize)
        {
            ct.ThrowIfCancellationRequested();

            // Идентификатор читается только здесь — для файлов, у которых уже совпал
            // размер, то есть для считанных единиц из всего просмотренного.
            var identified = sizeGroup
                .Select(f =>
                {
                    var (serial, id, links) = ReadIdentity(f.Path);
                    return f with
                    {
                        VolumeSerial = serial,
                        FileId = id,
                        Traits = links > 1 ? f.Traits | FileTraits.MultipleHardLinks : f.Traits,
                    };
                })
                .ToList();

            // Один физический файл, видимый по нескольким путям, дубликатом не является.
            // Это главная защита от потери данных: без неё файл, доступный через
            // соединение каталогов или жёсткую ссылку, был бы предложен как копия
            // самого себя, и его удаление уничтожило бы единственный экземпляр.
            var distinct = identified
                .Where(f => f.FileId != 0)
                .GroupBy(f => (f.VolumeSerial, f.FileId))
                .Select(g => g.First())
                .Concat(identified.Where(f => f.FileId == 0))
                .ToList();

            if (distinct.Count < 2)
            {
                continue;
            }

            // Шаг 2: начальный фрагмент. Дешёвая проверка, отсекающая непохожие.
            foreach (var partialGroup in distinct.GroupBy(f => ComputePartialHash(f.Path)).Where(g => g.Key is not null && g.Count() > 1))
            {
                ct.ThrowIfCancellationRequested();

                // Шаг 3: полное содержимое. Только для тех, кто дошёл досюда.
                foreach (var fullGroup in partialGroup.GroupBy(f => ComputeFullHash(f.Path, ct)).Where(g => g.Key is not null && g.Count() > 1))
                {
                    groups.Add(new DuplicateGroup(
                        fullGroup.OrderBy(f => f.LastWriteUtc).ToList(),
                        sizeGroup.Key));
                }
            }
        }

        return groups;
    }

    private static string? ComputePartialHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var buffer = new byte[PartialHashLength];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

            return Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ComputeFullHash(string path, CancellationToken ct)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var sha = SHA256.Create();

            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<CategoryUsage> SummarizeByCategory(List<ScannedFile> files)
        => files
            .GroupBy(f => CategoryOf(f.Path))
            .Select(g => new CategoryUsage(g.Key, g.Sum(f => f.SizeOnDiskBytes), g.Count()))
            .OrderByDescending(c => c.TotalBytes)
            .ToList();

    /// <summary>Вид файла человеческим языком.</summary>
    internal static string CategoryOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".webm" => "Видео",
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".heic" or ".webp" or ".raw" => "Изображения",
        ".mp3" or ".flac" or ".wav" or ".ogg" or ".m4a" or ".aac" => "Музыка",
        ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".iso" => "Архивы и образы",
        ".exe" or ".msi" or ".appx" or ".msix" => "Установщики и программы",
        ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" or ".rtf" or ".odt" => "Документы",
        ".dll" or ".sys" or ".pdb" or ".lib" or ".so" => "Служебные файлы программ",
        ".log" or ".tmp" or ".cache" or ".bak" or ".old" => "Временные файлы и журналы",
        _ => "Прочее",
    };

    private static string? TopLevelOf(string root, string filePath)
    {
        try
        {
            var relative = Path.GetRelativePath(root, filePath);
            var separator = relative.IndexOf(Path.DirectorySeparatorChar);

            return separator > 0 ? Path.Combine(root, relative[..separator]) : root;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BY_HANDLE_FILE_INFORMATION
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION lpFileInformation);
}
