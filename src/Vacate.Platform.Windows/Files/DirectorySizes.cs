using Vacate.Abstractions.Model;

namespace Vacate.Platform.Windows.Files;

/// <summary>
/// Размеры непосредственных детей каталога — данные для карты диска.
/// </summary>
/// <remarks>
/// Список отвечает на вопрос «что тут самое большое», карта — на вопрос «как это
/// соотносится между собой». Второй вопрос человек задаёт себе первым, глядя
/// на заполненный диск, и список на него отвечает плохо: строки «7,7 ГБ» и «1,4 ГБ»
/// выглядят одинаково, хотя различаются в пять раз.
///
/// Обход идёт вглубь по каждому ребёнку, и это дорого. Поэтому:
///
///   1. Точки повторной обработки не проходятся: соединение каталогов увело бы
///      обход в чужое место, и один и тот же файл посчитался бы дважды.
///   2. Облачные заглушки считаются по занимаемому месту, а оно у них нулевое:
///      файл числится в папке, но физически не скачан, и место на диске не занимает.
///   3. Недоступные каталоги пропускаются молча, но их число возвращается:
///      расхождение суммы с показаниями системы должно иметь объяснение.
/// </remarks>
public sealed class DirectorySizes
{
    /// <summary>Посчитать размеры непосредственных детей каталога.</summary>
    /// <param name="root">Каталог.</param>
    /// <param name="ct">Отмена.</param>
    public DirectoryMap Measure(string root, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var entries = new List<DirectoryEntry>();
        var skipped = 0;
        long looseFiles = 0;

        try
        {
            foreach (var child in Directory.GetDirectories(root))
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var attributes = File.GetAttributes(child);

                    // За соединения не идём: обход ушёл бы в чужое место,
                    // и то же самое посчиталось бы дважды.
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    var (size, unreadable) = MeasureRecursive(child, ct);

                    skipped += unreadable;

                    if (size > 0)
                    {
                        entries.Add(new DirectoryEntry(child, Path.GetFileName(child), size, IsDirectory: true));
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    skipped++;
                }
            }

            // Файлы, лежащие прямо здесь, тоже занимают место. Без них карта
            // не сходится с показаниями системы, и человек ищет несуществующую ошибку.
            foreach (var file in Directory.GetFiles(root))
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    looseFiles += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    skipped++;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return new DirectoryMap(root, [], 0, 1);
        }

        if (looseFiles > 0)
        {
            entries.Add(new DirectoryEntry(root, "файлы в этой папке", looseFiles, IsDirectory: false));
        }

        return new DirectoryMap(
            root,
            entries.OrderByDescending(e => e.SizeBytes).ToList(),
            entries.Sum(e => e.SizeBytes),
            skipped);
    }

    private static (long Size, int Skipped) MeasureRecursive(string path, CancellationToken ct)
    {
        long total = 0;
        var skipped = 0;

        var pending = new Stack<string>();
        pending.Push(path);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var current = pending.Pop();

            try
            {
                foreach (var directory in Directory.GetDirectories(current))
                {
                    if (!File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                    {
                        pending.Push(directory);
                    }
                }

                foreach (var file in Directory.GetFiles(current))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                    {
                        skipped++;
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                skipped++;
            }
        }

        return (total, skipped);
    }
}

/// <param name="Path">Полный путь.</param>
/// <param name="Name">Название для показа.</param>
/// <param name="SizeBytes">Занятое место со всем содержимым.</param>
/// <param name="IsDirectory">Каталог, а не сводка по отдельным файлам.</param>
public sealed record DirectoryEntry(string Path, string Name, long SizeBytes, bool IsDirectory);

/// <param name="Root">Каталог, для которого построена карта.</param>
/// <param name="Entries">Дети по убыванию размера.</param>
/// <param name="TotalBytes">Сумма.</param>
/// <param name="SkippedCount">
/// Сколько объектов не удалось прочитать. Молчаливый пропуск читается как «этого нет».
/// </param>
public sealed record DirectoryMap(string Root, IReadOnlyList<DirectoryEntry> Entries, long TotalBytes, int SkippedCount);
