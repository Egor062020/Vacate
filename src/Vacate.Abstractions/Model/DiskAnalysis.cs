namespace Vacate.Abstractions.Model;

/// <summary>
/// Файл, найденный при анализе диска.
/// </summary>
/// <param name="Path">Путь.</param>
/// <param name="SizeOnDiskBytes">Занимаемое место, а не логический размер.</param>
/// <param name="LastWriteUtc">Когда менялся.</param>
/// <param name="VolumeSerial">Серийный номер тома.</param>
/// <param name="FileId">
/// Идентификатор файла внутри тома. Вместе с номером тома однозначно определяет
/// физический файл: два пути с одинаковой парой — это один и тот же файл,
/// видимый через жёсткую ссылку или соединение каталогов, а не дубликат.
/// </param>
/// <param name="Traits">Особенности, влияющие на обращение с файлом.</param>
public sealed record ScannedFile(
    string Path,
    long SizeOnDiskBytes,
    DateTime LastWriteUtc,
    ulong VolumeSerial,
    ulong FileId,
    FileTraits Traits);

/// <summary>
/// Группа файлов с одинаковым содержимым.
/// </summary>
/// <param name="Files">Файлы группы. Первый считается оригиналом.</param>
/// <param name="FileSizeBytes">Размер одного файла.</param>
public sealed record DuplicateGroup(IReadOnlyList<ScannedFile> Files, long FileSizeBytes)
{
    /// <summary>Сколько места освободится, если оставить один экземпляр.</summary>
    public long RecoverableBytes => FileSizeBytes * Math.Max(0, Files.Count - 1);
}

/// <summary>Сколько места занимает определённый вид файлов.</summary>
/// <param name="Category">Вид файлов человеческим языком.</param>
/// <param name="TotalBytes">Суммарный объём.</param>
/// <param name="FileCount">Количество.</param>
public sealed record CategoryUsage(string Category, long TotalBytes, int FileCount);

/// <summary>Итог анализа занятого места.</summary>
public sealed record DiskAnalysisResult
{
    public required IReadOnlyList<ScannedFile> LargestFiles { get; init; }
    public required IReadOnlyList<DuplicateGroup> Duplicates { get; init; }
    public required IReadOnlyList<CategoryUsage> ByCategory { get; init; }
    public required IReadOnlyList<CategoryUsage> LargestDirectories { get; init; }

    public required int TotalFilesScanned { get; init; }
    public required long TotalBytesScanned { get; init; }

    /// <summary>
    /// Сколько объектов пропущено и почему: недоступные каталоги, соединения,
    /// облачные заглушки. Молчаливый пропуск читается как «этого нет».
    /// </summary>
    public required IReadOnlyList<string> SkipNotes { get; init; }

    /// <summary>Сколько места вернёт удаление лишних копий.</summary>
    public long RecoverableFromDuplicates => Duplicates.Sum(d => d.RecoverableBytes);
}
