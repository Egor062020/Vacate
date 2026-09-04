using System.Text.RegularExpressions;
using Microsoft.Win32;
using Vacate.Abstractions.Model;

namespace Vacate.Platform.Windows.Registry;

/// <summary>
/// Поиск остатков удалённой программы: папок и веток реестра, которые деинсталлятор не убрал.
/// </summary>
/// <remarks>
/// Опасность этого модуля не в том, что он мало найдёт, а в том, что найдёт лишнее.
/// Поиск по подстроке имени утаскивает чужие файлы: «Java» совпадает с посторонними
/// продуктами, а издатель вроде Microsoft или Google стоит у половины каталогов в системе.
///
/// Поэтому здесь три ограничителя:
///   1. общие системные каталоги исключены безусловно;
///   2. слишком короткие слова для поиска не используются — они совпадают со всем подряд;
///   3. для крупных издателей поиск по имени издателя отключён совсем.
///
/// И главное: результат делится по уровню уверенности, а объекты уровня «возможно»
/// по умолчанию не отмечены. Пользователь должен видеть основание, а не просто список.
/// </remarks>
public sealed partial class LeftoverScanner
{
    /// <summary>Слово короче этого совпадает со слишком многим и для поиска не годится.</summary>
    private const int MinimumTokenLength = 5;

    /// <summary>
    /// Издатели, у которых десятки продуктов и общие каталоги. Искать их остатки
    /// по имени издателя — гарантированно утащить чужое.
    /// </summary>
    private static readonly string[] BroadPublishers =
    [
        "microsoft", "google", "intel", "nvidia", "amd", "adobe", "apple",
        "oracle", "realtek", "dell", "hp", "lenovo", "asus", "canonical",
    ];

    /// <summary>Каталоги, которые никогда не считаются остатками.</summary>
    private static readonly string[] NeverLeftovers =
    [
        "common files", "windows", "windowsapps", "system32", "syswow64",
        "microsoft", "microsoft shared", "internet explorer", "windows defender",
        "windows nt", "package cache", "temp", "programdata",
    ];

    [GeneratedRegex(@"\s*\(?(x64|x86|64-bit|32-bit)\)?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ArchitectureSuffix();

    [GeneratedRegex(@"\s*\d+(\.\d+)*\s*")]
    private static partial Regex VersionNumbers();

    [GeneratedRegex(@"\s*\([^)]*\)\s*")]
    private static partial Regex Parentheses();

    /// <summary>Найти вероятные остатки программы.</summary>
    /// <param name="app">Программа, которую удалили.</param>
    /// <param name="ct">Отмена.</param>
    public IReadOnlyList<LeftoverItem> Scan(InstalledApp app, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        var results = new List<LeftoverItem>();
        var nameToken = BuildSearchToken(app.DisplayName, app.Publisher);
        var publisherToken = BuildPublisherToken(app.Publisher);

        // Каталог установки, если программа его называла, — самый надёжный след.
        if (!string.IsNullOrWhiteSpace(app.InstallLocation) && Directory.Exists(app.InstallLocation))
        {
            results.Add(new LeftoverItem(
                app.InstallLocation,
                LeftoverKind.Directory,
                MeasureDirectory(app.InstallLocation, ct),
                LeftoverConfidence.Certain,
                ["каталог установки, указанный самой программой"]));
        }

        foreach (var root in EnumerateSearchRoots())
        {
            ct.ThrowIfCancellationRequested();
            ScanDirectoryRoot(root, app, nameToken, publisherToken, results, ct);
        }

        ScanRegistry(app, nameToken, publisherToken, results, ct);

        return results;
    }

    private static IEnumerable<string> EnumerateSearchRoots()
    {
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        };

        return candidates.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)).Distinct();
    }

    private void ScanDirectoryRoot(
        string root,
        InstalledApp app,
        string? nameToken,
        string? publisherToken,
        List<LeftoverItem> results,
        CancellationToken ct)
    {
        string[] directories;

        try
        {
            // Только верхний уровень: остатки программ лежат папками прямо в этих каталогах,
            // а рекурсивный обход дал бы часы работы и лавину ложных совпадений.
            directories = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return;
        }

        foreach (var directory in directories)
        {
            ct.ThrowIfCancellationRequested();

            var name = Path.GetFileName(directory);

            if (IsNeverLeftover(name))
            {
                continue;
            }

            // Уже добавлен как каталог установки.
            if (results.Any(r => string.Equals(r.Path, directory, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var match = Classify(name, nameToken, publisherToken, app);

            if (match is not null)
            {
                results.Add(new LeftoverItem(
                    directory,
                    LeftoverKind.Directory,
                    MeasureDirectory(directory, ct),
                    match.Value.Confidence,
                    match.Value.Evidence));
            }
        }
    }

    private void ScanRegistry(
        InstalledApp app,
        string? nameToken,
        string? publisherToken,
        List<LeftoverItem> results,
        CancellationToken ct)
    {
        var roots = new (RegistryHive Hive, string Label)[]
        {
            (RegistryHive.CurrentUser, "HKCU"),
            (RegistryHive.LocalMachine, "HKLM"),
        };

        foreach (var (hive, label) in roots)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var software = baseKey.OpenSubKey("SOFTWARE");

                if (software is null)
                {
                    continue;
                }

                foreach (var keyName in software.GetSubKeyNames())
                {
                    ct.ThrowIfCancellationRequested();

                    if (IsNeverLeftover(keyName))
                    {
                        continue;
                    }

                    var match = Classify(keyName, nameToken, publisherToken, app);

                    if (match is not null)
                    {
                        results.Add(new LeftoverItem(
                            $@"{label}\SOFTWARE\{keyName}",
                            LeftoverKind.RegistryKey,
                            0,
                            match.Value.Confidence,
                            match.Value.Evidence));
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                // Часть веток недоступна даже администратору. Это нормально.
            }
        }
    }

    private static (LeftoverConfidence Confidence, IReadOnlyList<string> Evidence)? Classify(
        string candidateName,
        string? nameToken,
        string? publisherToken,
        InstalledApp app)
    {
        var normalized = candidateName.ToLowerInvariant();

        if (nameToken is not null)
        {
            if (string.Equals(normalized, nameToken, StringComparison.Ordinal))
            {
                return (LeftoverConfidence.Likely, [$"имя совпадает с названием программы «{app.DisplayName}»"]);
            }

            if (normalized.Contains(nameToken, StringComparison.Ordinal))
            {
                return (LeftoverConfidence.Possible, [$"в имени встречается «{nameToken}» из названия программы"]);
            }
        }

        if (publisherToken is not null && string.Equals(normalized, publisherToken, StringComparison.Ordinal))
        {
            // Каталог издателя может быть общим для нескольких его программ,
            // поэтому это самый слабый уровень и по умолчанию он не отмечается.
            return (LeftoverConfidence.Possible,
                [$"имя совпадает с издателем «{app.Publisher}»", "у издателя могут быть другие программы в этом каталоге"]);
        }

        return null;
    }

    /// <summary>
    /// Слово для поиска, очищенное от версий, разрядности и уточнений в скобках.
    /// </summary>
    /// <remarks>
    /// «FreeCAD 1.1.1 (Установлено для текущего пользователя)» превращается в «freecad».
    /// Без очистки не нашлось бы ничего: на диске каталог называется просто именем программы.
    /// </remarks>
    internal static string? BuildSearchToken(string displayName, string? publisher = null)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var cleaned = Parentheses().Replace(displayName, " ");
        cleaned = ArchitectureSuffix().Replace(cleaned, " ");
        cleaned = VersionNumbers().Replace(cleaned, " ");
        cleaned = cleaned.Trim().ToLowerInvariant();

        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Названия часто начинаются с имени крупного производителя: «Google Chrome»,
        // «Microsoft Edge», «Adobe Acrobat». Брать оттуда первое слово нельзя — получится
        // имя издателя, чей каталог общий для всех его продуктов. Проверка на живой машине
        // показала цену ошибки: для Chrome предлагалось удалить каталог Google целиком,
        // то есть данные Диска, Earth и всего остального заодно.
        //
        // Пропускаются ТОЛЬКО крупные издатели из списка. Совпадение с полем «издатель»
        // здесь не годится: у однопродуктовых компаний название программы и имя издателя
        // совпадают («Obsidian» от «Obsidian.md»), и такая проверка выбрасывала бы
        // единственное пригодное слово. На Telegram это давало поиск по слову «desktop»,
        // которое совпадает со слишком многим.
        foreach (var word in words)
        {
            if (BroadPublishers.Contains(word))
            {
                continue;
            }

            return word.Length >= MinimumTokenLength ? word : null;
        }

        return null;
    }

    internal static string? BuildPublisherToken(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
        {
            return null;
        }

        var cleaned = Parentheses().Replace(publisher, " ").Trim().ToLowerInvariant();

        // Точка разделяет наравне с пробелом и запятой: издатели сплошь и рядом
        // записываются доменным именем («Obsidian.md», «Example.com»), а каталог
        // на диске при этом называется без суффикса.
        var firstWord = cleaned.Split([' ', ',', '.'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

        if (firstWord is null || firstWord.Length < MinimumTokenLength)
        {
            return null;
        }

        // У крупных издателей десятки продуктов и общие каталоги:
        // поиск по такому имени утащит чужое.
        return BroadPublishers.Contains(firstWord) ? null : firstWord;
    }

    private static bool IsNeverLeftover(string name)
        => NeverLeftovers.Contains(name.ToLowerInvariant());

    private static long MeasureDirectory(string path, CancellationToken ct)
    {
        try
        {
            long total = 0;

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Отдельный недоступный файл не должен обнулять оценку каталога.
                }
            }

            return total;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return 0;
        }
    }
}
