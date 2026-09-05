using System.Text.Json;
using Vacate.Abstractions.Model;

namespace Vacate.Platform.Windows.Registry;

/// <summary>
/// Слежение за установкой: снимок системы до и после, чтобы потом удалить ровно то,
/// что появилось.
/// </summary>
/// <remarks>
/// Смысл в том, чтобы не гадать. Обычный поиск следов работает по совпадению имён
/// и потому осторожничает: слишком короткие слова не берёт, каталоги крупных издателей
/// не трогает. Снимок до и после даёт другое основание — «этого здесь не было
/// пятнадцать минут назад», и оно не зависит от того, как называется программа.
///
/// Границы честные, и о них надо сказать вслух:
///
///   1. Сравниваются каталоги верхнего уровня и ветви реестра, а не каждый файл.
///      Полный снимок диска занял бы десятки минут и гигабайты — ради удаления
///      одной программы это неразумно.
///   2. В промежутке между снимками работает не только установщик: система обновляется,
///      браузер пишет кэш, антивирус обновляет базы. Поэтому появившееся показывается
///      человеку списком, а не удаляется молча.
///   3. Снимок «до» надо успеть сделать ДО запуска установщика. Если программа уже
///      установлена, сравнивать не с чем, и честнее сказать это, чем выдать
///      случайные различия за находки.
/// </remarks>
public sealed class InstallWatcher
{
    private static string WatchDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vacate", "watch");

    /// <summary>Сделать снимок состояния системы.</summary>
    public SystemSnapshot Capture(CancellationToken ct = default)
    {
        var directories = new List<string>();

        foreach (var root in EnumerateRoots())
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                directories.AddRange(Directory.GetDirectories(root));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                // Недоступный корень просто не участвует в сравнении.
            }
        }

        return new SystemSnapshot(
            DateTime.UtcNow,
            directories,
            ReadRegistryKeys(ct),
            new InstalledAppsScanner().Scan(ct).Select(a => a.Id).ToList());
    }

    /// <summary>Сохранить снимок, чтобы сравнить с ним после установки.</summary>
    public string Save(SystemSnapshot snapshot, string label)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Directory.CreateDirectory(WatchDirectory);

        var path = Path.Combine(WatchDirectory, $"{label}.json");

        File.WriteAllText(path, JsonSerializer.Serialize(snapshot));

        return path;
    }

    /// <summary>Прочитать ранее сохранённый снимок.</summary>
    public SystemSnapshot? Load(string label)
    {
        var path = Path.Combine(WatchDirectory, $"{label}.json");

        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<SystemSnapshot>(File.ReadAllText(path))
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Список сохранённых наблюдений.</summary>
    public IReadOnlyList<string> List()
    {
        try
        {
            return Directory.Exists(WatchDirectory)
                ? Directory.GetFiles(WatchDirectory, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Удалить наблюдение.</summary>
    public void Forget(string label)
    {
        try
        {
            var path = Path.Combine(WatchDirectory, $"{label}.json");

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Останется лежать; вреда от этого нет.
        }
    }

    /// <summary>Что появилось между снимками.</summary>
    public InstallDifference Compare(SystemSnapshot before, SystemSnapshot after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var directories = after.Directories
            .Except(before.Directories, StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var keys = after.RegistryKeys
            .Except(before.RegistryKeys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var apps = after.InstalledAppIds
            .Except(before.InstalledAppIds, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new InstallDifference(directories, keys, apps, before.TakenAtUtc, after.TakenAtUtc);
    }

    private static IEnumerable<string> EnumerateRoots()
    {
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        };

        return candidates.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)).Distinct();
    }

    private static List<string> ReadRegistryKeys(CancellationToken ct)
    {
        var keys = new List<string>();

        var roots = new (Microsoft.Win32.RegistryHive Hive, string Label)[]
        {
            (Microsoft.Win32.RegistryHive.CurrentUser, "HKCU"),
            (Microsoft.Win32.RegistryHive.LocalMachine, "HKLM"),
        };

        foreach (var (hive, label) in roots)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, Microsoft.Win32.RegistryView.Registry64);
                using var software = baseKey.OpenSubKey("SOFTWARE");

                if (software is null)
                {
                    continue;
                }

                keys.AddRange(software.GetSubKeyNames().Select(name => $@"{label}\SOFTWARE\{name}"));
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                // Часть ветвей недоступна — сравнение пройдёт по остальным.
            }
        }

        return keys;
    }
}

/// <summary>Состояние системы в момент времени.</summary>
/// <param name="TakenAtUtc">Когда сделан снимок.</param>
/// <param name="Directories">Каталоги верхнего уровня в местах установки программ.</param>
/// <param name="RegistryKeys">Ветви реестра верхнего уровня.</param>
/// <param name="InstalledAppIds">Идентификаторы записей в списке установленного.</param>
public sealed record SystemSnapshot(
    DateTime TakenAtUtc,
    IReadOnlyList<string> Directories,
    IReadOnlyList<string> RegistryKeys,
    IReadOnlyList<string> InstalledAppIds);

/// <summary>Что появилось между двумя снимками.</summary>
public sealed record InstallDifference(
    IReadOnlyList<string> NewDirectories,
    IReadOnlyList<string> NewRegistryKeys,
    IReadOnlyList<string> NewApps,
    DateTime FromUtc,
    DateTime ToUtc)
{
    /// <summary>Появилось ли хоть что-то.</summary>
    public bool IsEmpty => NewDirectories.Count == 0 && NewRegistryKeys.Count == 0 && NewApps.Count == 0;
}
