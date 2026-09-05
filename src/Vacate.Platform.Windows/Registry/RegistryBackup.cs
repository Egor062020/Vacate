using System.Diagnostics;
using Vacate.Abstractions.Model;

namespace Vacate.Platform.Windows.Registry;

/// <summary>
/// Резервная копия ветвей реестра перед их удалением.
/// </summary>
/// <remarks>
/// Единственная часть продукта, которую карантин не покрывал: файл можно передвинуть
/// и вернуть, ветку реестра — нет. До сих пор об этом честно предупреждали перед нажатием,
/// но предупреждение не заменяет возможности всё вернуть.
///
/// Копия делается штатной выгрузкой в файл .reg. Способ выбран не от бедности:
///
///   1. Файл читается человеком в блокноте — он видит, что именно сохранено,
///      и не обязан верить программе на слово.
///   2. Восстановление работает без нас: двойной щелчок по файлу вернёт ветку,
///      даже если программа к тому времени удалена.
///   3. Формат не изменится: он старше большинства пользователей этой программы.
///
/// Копия делается ДО удаления и только один раз на сеанс — иначе на плане из тридцати
/// веток мы тридцать раз запустили бы внешнюю программу.
/// </remarks>
public sealed class RegistryBackup
{
    /// <summary>Куда складываются копии.</summary>
    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vacate", "registry-backup");

    /// <summary>
    /// Сохранить ветви плана в файл.
    /// </summary>
    /// <param name="plan">План. Из него берутся все удаляемые ветви.</param>
    /// <param name="ct">Отмена.</param>
    /// <returns>Путь к файлу или пустая ссылка, если сохранять было нечего.</returns>
    public async Task<BackupResult?> SaveAsync(MutationPlan plan, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var keys = plan.AllOperations
            .OfType<DeleteRegistryOperation>()
            .Select(o => o.Target)
            .Where(t => t.IsWholeKey)
            .Select(Format)
            .Where(k => k is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (keys.Count == 0)
        {
            return null;
        }

        System.IO.Directory.CreateDirectory(Directory);

        // Имя по времени: человек ищет копию по дате, а не по внутреннему
        // идентификатору плана, о существовании которого он не знает.
        var path = Path.Combine(Directory, $"{DateTime.Now:yyyyMMdd-HHmmss}.reg");
        var saved = new List<string>();
        var failed = new List<string>();

        foreach (var key in keys)
        {
            ct.ThrowIfCancellationRequested();

            // Каждая ветка выгружается во временный файл, потом всё сшивается:
            // штатная выгрузка не умеет дописывать в существующий файл.
            var part = Path.Combine(Path.GetTempPath(), $"vacate-reg-{Guid.NewGuid():N}.reg");

            if (await ExportAsync(key!, part, ct).ConfigureAwait(false))
            {
                saved.Add(part);
            }
            else
            {
                failed.Add(key!);
            }
        }

        if (saved.Count == 0)
        {
            return new BackupResult(null, 0, failed);
        }

        await MergeAsync(path, saved, ct).ConfigureAwait(false);

        foreach (var part in saved)
        {
            Discard(part);
        }

        return new BackupResult(path, saved.Count, failed);
    }

    /// <summary>Записать ветвь в файл штатной выгрузкой.</summary>
    private static async Task<bool> ExportAsync(string key, string path, CancellationToken ct)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe"),

                // «/y» перезаписывает без вопроса: файл наш собственный и только что создан.
                Arguments = $"export \"{key}\" \"{path}\" /y",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            return process.ExitCode == 0 && File.Exists(path);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Сшить выгрузки в один файл.</summary>
    private static async Task MergeAsync(string path, IReadOnlyList<string> parts, CancellationToken ct)
    {
        // Кодировка обязательна: файлы .reg в этом формате читаются системой
        // только как UTF-16, и файл в другой кодировке она молча отвергнет.
        await using var writer = new StreamWriter(path, append: false, System.Text.Encoding.Unicode);

        await writer.WriteLineAsync("Windows Registry Editor Version 5.00").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);
        await writer.WriteLineAsync("; Резервная копия, созданная Vacate перед удалением следов программы.").ConfigureAwait(false);
        await writer.WriteLineAsync("; Чтобы вернуть удалённое, откройте этот файл двойным щелчком.").ConfigureAwait(false);
        await writer.WriteLineAsync().ConfigureAwait(false);

        foreach (var part in parts)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var lines = await File.ReadAllLinesAsync(part, System.Text.Encoding.Unicode, ct).ConfigureAwait(false);

                // Заголовок формата в файле должен быть ровно один.
                foreach (var line in lines.Where(l => !l.StartsWith("Windows Registry Editor", StringComparison.Ordinal)))
                {
                    await writer.WriteLineAsync(line).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Часть не прочиталась — остальные всё равно сохраним.
            }
        }
    }

    /// <summary>Привести цель к виду, понятному штатной выгрузке.</summary>
    internal static string? Format(RegistryTarget target)
    {
        if (target is null || string.IsNullOrWhiteSpace(target.SubKeyPath))
        {
            return null;
        }

        var hive = target.Hive switch
        {
            RegistryHiveKind.LocalMachine => "HKLM",
            RegistryHiveKind.CurrentUser => "HKCU",
            RegistryHiveKind.Users => "HKU",
            _ => null,
        };

        return hive is null ? null : $@"{hive}\{target.SubKeyPath.Trim('\\')}";
    }

    private static void Discard(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Останется во временной папке и уйдёт с обычной очисткой.
        }
    }
}

/// <param name="Path">Файл копии. Пустая ссылка, если сохранить не удалось ничего.</param>
/// <param name="SavedCount">Сколько ветвей сохранено.</param>
/// <param name="Failed">Ветви, которые сохранить не удалось. Человек должен знать их поимённо.</param>
public sealed record BackupResult(string? Path, int SavedCount, IReadOnlyList<string> Failed);
