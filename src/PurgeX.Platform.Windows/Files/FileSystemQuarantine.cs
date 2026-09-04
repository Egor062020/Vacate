using System.Text.Json;
using PurgeX.Abstractions.Model;
using PurgeX.Abstractions.Safety;

namespace PurgeX.Platform.Windows.Files;

/// <summary>
/// Карантин на файловой системе: по одному хранилищу на каждом томе.
/// </summary>
/// <remarks>
/// Размещение по томам — не деталь реализации, а требование. Перемещение файла мгновенно
/// только внутри одного тома; попытка сложить файл с диска D: в карантин на C: означала бы
/// копирование с удвоением занятого места, а на заполненном диске — просто отказ.
/// Именно на заполненном диске продукт и нужен чаще всего.
/// </remarks>
public sealed class FileSystemQuarantine : IQuarantine
{
    /// <summary>Имя служебного каталога в корне каждого тома.</summary>
    public const string DirectoryName = "$PurgeX.Quarantine";

    private const string ManifestFileName = "manifest.jsonl";

    private readonly TimeSpan _retention;
    private readonly long _budgetPerVolumeBytes;
    private readonly string? _storeOverride;
    private readonly object _manifestLock = new();

    /// <param name="retention">Сколько хранится содержимое карантина.</param>
    /// <param name="budgetPerVolumeBytes">Предел объёма карантина на одном томе.</param>
    /// <param name="storeOverride">
    /// Расположение хранилища вместо корня тома. Нужно тестам: создание каталога
    /// в корне системного диска требует прав администратора, а проверка механизма отката
    /// не должна от них зависеть.
    /// </param>
    public FileSystemQuarantine(
        TimeSpan? retention = null,
        long budgetPerVolumeBytes = 2L * 1024 * 1024 * 1024,
        string? storeOverride = null)
    {
        _retention = retention ?? TimeSpan.FromDays(30);
        _budgetPerVolumeBytes = budgetPerVolumeBytes;
        _storeOverride = storeOverride;
    }

    public async Task<QuarantineResult> StoreAsync(FileTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);

        try
        {
            var volumeRoot = Path.GetPathRoot(Path.GetFullPath(target.Path));

            if (string.IsNullOrEmpty(volumeRoot))
            {
                return new QuarantineResult(false, Reason: LocalizedText.FromResource("Quarantine.NoVolume"));
            }

            var storeDirectory = EnsureStore(_storeOverride ?? volumeRoot);

            // Бюджет: тихого вытеснения старых записей не происходит никогда.
            // Пользователь получает выбор, а не молча теряет возможность отката.
            var used = GetVolumeUsage(storeDirectory);

            if (used + target.SizeOnDiskBytes > _budgetPerVolumeBytes)
            {
                return new QuarantineResult(
                    false,
                    Reason: LocalizedText.FromResource("Quarantine.BudgetExceeded", volumeRoot),
                    BudgetExceeded: true);
            }

            var token = Guid.NewGuid().ToString("N");
            var storedPath = Path.Combine(storeDirectory, token);

            var attributes = File.GetAttributes(target.Path);

            // Атрибут «только для чтения» иначе не даст ни переместить, ни удалить файл,
            // и объект ошибочно попал бы в отчёт как занятый.
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(target.Path, attributes & ~FileAttributes.ReadOnly);
            }

            if (target.IsDirectory)
            {
                Directory.Move(target.Path, storedPath);
            }
            else
            {
                File.Move(target.Path, storedPath, overwrite: false);
            }

            var record = new QuarantineRecord(
                token,
                target.Path,
                target.IsDirectory,
                target.SizeOnDiskBytes,
                (int)attributes,
                DateTime.UtcNow,
                DateTime.UtcNow.Add(_retention));

            await AppendManifestAsync(storeDirectory, record, ct).ConfigureAwait(false);

            return new QuarantineResult(true, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new QuarantineResult(false, Reason: LocalizedText.FromResource("Quarantine.MoveFailed", ex.Message));
        }
    }

    public async Task<RestoreResult> RestoreAsync(string undoToken, CancellationToken ct)
    {
        foreach (var storeDirectory in GetStores())
        {
            var record = await FindRecordAsync(storeDirectory, undoToken, ct).ConfigureAwait(false);

            if (record is null)
            {
                continue;
            }

            var storedPath = Path.Combine(storeDirectory, record.Token);

            if (!File.Exists(storedPath) && !Directory.Exists(storedPath))
            {
                // Файл мог быть изъят антивирусом или удалён вручную. Честно говорим об этом,
                // а не рапортуем об успешном восстановлении.
                return new RestoreResult(false, Reason: LocalizedText.FromResource("Quarantine.DataGone", record.OriginalPath));
            }

            try
            {
                var parent = Path.GetDirectoryName(record.OriginalPath);

                if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                {
                    // За время карантина исходный каталог мог исчезнуть.
                    Directory.CreateDirectory(parent);
                }

                if (record.IsDirectory)
                {
                    Directory.Move(storedPath, record.OriginalPath);
                }
                else
                {
                    File.Move(storedPath, record.OriginalPath, overwrite: false);
                }

                File.SetAttributes(record.OriginalPath, (FileAttributes)record.Attributes);

                return new RestoreResult(true, AttributesRestored: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new RestoreResult(false, Reason: LocalizedText.FromResource("Quarantine.RestoreFailed", ex.Message));
            }
        }

        return new RestoreResult(false, Reason: LocalizedText.FromResource("Quarantine.TokenNotFound"));
    }

    public async Task<int> PurgeExpiredAsync(CancellationToken ct)
    {
        var removed = 0;
        var now = DateTime.UtcNow;

        foreach (var storeDirectory in GetStores())
        {
            var records = await ReadManifestAsync(storeDirectory, ct).ConfigureAwait(false);
            var survivors = new List<QuarantineRecord>();

            foreach (var record in records)
            {
                if (record.ExpiresAtUtc > now)
                {
                    survivors.Add(record);
                    continue;
                }

                var storedPath = Path.Combine(storeDirectory, record.Token);

                try
                {
                    if (Directory.Exists(storedPath))
                    {
                        Directory.Delete(storedPath, recursive: true);
                    }
                    else if (File.Exists(storedPath))
                    {
                        File.Delete(storedPath);
                    }

                    removed++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Не смогли убрать сейчас — попробуем в следующий раз, запись сохраняем.
                    survivors.Add(record);
                }
            }

            await RewriteManifestAsync(storeDirectory, survivors, ct).ConfigureAwait(false);
        }

        return removed;
    }

    public async Task<IReadOnlyDictionary<string, long>> GetUsageByVolumeAsync(CancellationToken ct)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var storeDirectory in GetStores())
        {
            var root = Path.GetPathRoot(storeDirectory) ?? storeDirectory;
            result[root] = GetVolumeUsage(storeDirectory);
        }

        await Task.CompletedTask.ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Является ли путь частью карантина.
    /// </summary>
    /// <remarks>
    /// Обязательная проверка: без неё карта диска покажет карантин самой большой папкой,
    /// а поиск дубликатов найдёт карантинные копии и предложит удалить оригинал.
    /// </remarks>
    public bool IsQuarantinePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(path);
            return full.Contains(DirectoryName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>Хранилища, с которыми работает этот экземпляр.</summary>
    private IEnumerable<string> GetStores()
    {
        if (_storeOverride is not null)
        {
            var overridden = Path.Combine(_storeOverride, DirectoryName);

            if (Directory.Exists(overridden))
            {
                yield return overridden;
            }

            yield break;
        }

        foreach (var store in EnumerateStores())
        {
            yield return store;
        }
    }

    /// <summary>Каталоги карантина на всех доступных томах. Нужны политике путей как запрещённые.</summary>
    public static IEnumerable<string> EnumerateStores()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            {
                continue;
            }

            var candidate = Path.Combine(drive.RootDirectory.FullName, DirectoryName);

            if (Directory.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static string EnsureStore(string volumeRoot)
    {
        var directory = Path.Combine(volumeRoot, DirectoryName);

        if (!Directory.Exists(directory))
        {
            var info = Directory.CreateDirectory(directory);
            info.Attributes |= FileAttributes.Hidden | FileAttributes.System;
        }

        return directory;
    }

    private static long GetVolumeUsage(string storeDirectory)
    {
        try
        {
            return new DirectoryInfo(storeDirectory)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private async Task AppendManifestAsync(string storeDirectory, QuarantineRecord record, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(record, QuarantineJsonContext.Default.QuarantineRecord);
        var manifestPath = Path.Combine(storeDirectory, ManifestFileName);

        // Простая блокировка: карантин пишется из одного процесса-исполнителя.
        lock (_manifestLock)
        {
            File.AppendAllText(manifestPath, line + Environment.NewLine);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async Task<List<QuarantineRecord>> ReadManifestAsync(string storeDirectory, CancellationToken ct)
    {
        var manifestPath = Path.Combine(storeDirectory, ManifestFileName);
        var records = new List<QuarantineRecord>();

        if (!File.Exists(manifestPath))
        {
            return records;
        }

        foreach (var line in await File.ReadAllLinesAsync(manifestPath, ct).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var record = JsonSerializer.Deserialize(line, QuarantineJsonContext.Default.QuarantineRecord);

                if (record is not null)
                {
                    records.Add(record);
                }
            }
            catch (JsonException)
            {
                // Повреждённая строка не должна ронять весь карантин:
                // остальные записи по-прежнему пригодны для восстановления.
            }
        }

        return records;
    }

    private static async Task<QuarantineRecord?> FindRecordAsync(string storeDirectory, string token, CancellationToken ct)
    {
        var records = await ReadManifestAsync(storeDirectory, ct).ConfigureAwait(false);
        return records.FirstOrDefault(r => r.Token == token);
    }

    private async Task RewriteManifestAsync(string storeDirectory, List<QuarantineRecord> records, CancellationToken ct)
    {
        var manifestPath = Path.Combine(storeDirectory, ManifestFileName);
        var lines = records.Select(r => JsonSerializer.Serialize(r, QuarantineJsonContext.Default.QuarantineRecord));

        lock (_manifestLock)
        {
            File.WriteAllLines(manifestPath, lines);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>Запись о помещённом в карантин объекте.</summary>
public sealed record QuarantineRecord(
    string Token,
    string OriginalPath,
    bool IsDirectory,
    long SizeOnDiskBytes,
    int Attributes,
    DateTime StoredAtUtc,
    DateTime ExpiresAtUtc);
