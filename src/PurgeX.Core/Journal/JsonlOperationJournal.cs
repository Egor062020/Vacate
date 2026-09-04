using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using PurgeX.Abstractions.Safety;

namespace PurgeX.Core.Journal;

/// <summary>
/// Журнал операций в виде текстового файла с построчными записями.
/// </summary>
/// <remarks>
/// Выбор формата обоснован так. База данных потребовала бы вспомогательной библиотеки,
/// которую пришлось бы распаковывать во временный каталог — то есть ровно туда, где продукт
/// чистит, и куда на части корпоративных машин запрещено класть исполняемый код.
/// Текстовый формат от этого свободен и читается любым редактором, что для журнала,
/// которому пользователь должен доверять, скорее достоинство.
///
/// Возражение «двести тысяч удалённых файлов дадут двести тысяч строк» снято тем,
/// что записи ведутся ГРУППАМИ. Поэлементно хранится только то, что действительно
/// можно вернуть, — объекты в карантине.
/// </remarks>
public sealed class JsonlOperationJournal : IOperationJournal
{
    private readonly string _directory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public JsonlOperationJournal(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        Directory.CreateDirectory(_directory);
    }

    private string SessionsPath => Path.Combine(_directory, "sessions.jsonl");

    private string UndoablePath(string sessionId) => Path.Combine(_directory, $"undo-{sessionId}.jsonl");

    private string GroupsPath(string sessionId) => Path.Combine(_directory, $"groups-{sessionId}.jsonl");

    public async Task<string> BeginSessionAsync(string origin, CancellationToken ct)
    {
        var sessionId = $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";

        await AppendAsync(SessionsPath, new SessionRecord(
            sessionId,
            origin,
            DateTime.UtcNow,
            FinishedAtUtc: null,
            ClaimedBytes: 0,
            ActuallyFreedBytes: 0,
            ItemCount: 0,
            HasRestorableItems: false,
            WasEmergencyMode: false), ct).ConfigureAwait(false);

        return sessionId;
    }

    public Task RecordGroupAsync(string sessionId, GroupJournalEntry entry, CancellationToken ct)
        => AppendAsync(GroupsPath(sessionId), new GroupRecord(
            entry.GroupId,
            entry.TitleKey,
            entry.ItemCount,
            entry.ClaimedBytes,
            entry.Succeeded,
            entry.Skipped,
            entry.Failed), ct);

    public Task RecordUndoableAsync(string sessionId, UndoableEntry entry, CancellationToken ct)
        => AppendAsync(UndoablePath(sessionId), new UndoRecord(
            entry.UndoToken,
            entry.OriginalPath,
            entry.Kind.ToString(),
            entry.SizeOnDiskBytes,
            entry.ExpiresAtUtc,
            Restored: false), ct);

    public Task CompleteSessionAsync(string sessionId, SessionSummary summary, CancellationToken ct)
        => AppendAsync(SessionsPath, new SessionRecord(
            summary.SessionId,
            summary.Origin,
            summary.StartedAtUtc,
            summary.FinishedAtUtc,
            summary.ClaimedBytes,
            summary.ActuallyFreedBytes,
            summary.ItemCount,
            summary.HasRestorableItems,
            summary.WasEmergencyMode), ct);

    public async Task<IReadOnlyList<SessionSummary>> GetRecentSessionsAsync(int limit, CancellationToken ct)
    {
        var records = await ReadAllAsync(SessionsPath, JournalJsonContext.Default.SessionRecord, ct).ConfigureAwait(false);

        // Одна сессия пишется дважды: при начале и при завершении. Берём последнюю запись,
        // чтобы в истории оказался итог, а не заготовка.
        return records
            .GroupBy(r => r.SessionId)
            .Select(g => g.Last())
            .OrderByDescending(r => r.StartedAtUtc)
            .Take(limit)
            .Select(r => new SessionSummary
            {
                SessionId = r.SessionId,
                Origin = r.Origin,
                StartedAtUtc = r.StartedAtUtc,
                FinishedAtUtc = r.FinishedAtUtc,
                ClaimedBytes = r.ClaimedBytes,
                ActuallyFreedBytes = r.ActuallyFreedBytes,
                ItemCount = r.ItemCount,
                HasRestorableItems = r.HasRestorableItems,
                WasEmergencyMode = r.WasEmergencyMode,
            })
            .ToList();
    }

    public async Task<IReadOnlyList<UndoableEntry>> GetUndoableAsync(string sessionId, CancellationToken ct)
    {
        var records = await ReadAllAsync(UndoablePath(sessionId), JournalJsonContext.Default.UndoRecord, ct).ConfigureAwait(false);

        return records
            .GroupBy(r => r.UndoToken)
            .Select(g => g.Last())
            .Where(r => !r.Restored)
            .Select(r => new UndoableEntry(
                r.UndoToken,
                r.OriginalPath,
                Enum.TryParse<UndoableKind>(r.Kind, out var kind) ? kind : UndoableKind.QuarantinedFile,
                r.SizeOnDiskBytes,
                r.ExpiresAtUtc))
            .ToList();
    }

    public async Task MarkRestoredAsync(string sessionId, string undoToken, CancellationToken ct)
    {
        var records = await ReadAllAsync(UndoablePath(sessionId), JournalJsonContext.Default.UndoRecord, ct).ConfigureAwait(false);
        var target = records.LastOrDefault(r => r.UndoToken == undoToken);

        if (target is null)
        {
            return;
        }

        // Записи не переписываются, а дополняются: история должна показывать
        // и то, что было откачено, а не делать вид, что этого не происходило.
        await AppendAsync(UndoablePath(sessionId), target with { Restored = true }, ct).ConfigureAwait(false);
    }

    private async Task AppendAsync<T>(string path, T record, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(record, typeof(T), JournalJsonContext.Default);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            await File.AppendAllTextAsync(path, line + Environment.NewLine, ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<List<T>> ReadAllAsync<T>(string path, JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        var result = new List<T>();

        if (!File.Exists(path))
        {
            return result;
        }

        foreach (var line in await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var record = JsonSerializer.Deserialize(line, typeInfo);

                if (record is not null)
                {
                    result.Add(record);
                }
            }
            catch (JsonException)
            {
                // Повреждение одной строки не должно делать нечитаемым весь журнал:
                // остальные записи по-прежнему годятся для отката.
            }
        }

        return result;
    }
}

internal sealed record SessionRecord(
    string SessionId,
    string Origin,
    DateTime StartedAtUtc,
    DateTime? FinishedAtUtc,
    long ClaimedBytes,
    long ActuallyFreedBytes,
    int ItemCount,
    bool HasRestorableItems,
    bool WasEmergencyMode);

internal sealed record GroupRecord(
    string GroupId,
    string TitleKey,
    int ItemCount,
    long ClaimedBytes,
    int Succeeded,
    int Skipped,
    int Failed);

internal sealed record UndoRecord(
    string UndoToken,
    string OriginalPath,
    string Kind,
    long SizeOnDiskBytes,
    DateTime ExpiresAtUtc,
    bool Restored);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(SessionRecord))]
[JsonSerializable(typeof(GroupRecord))]
[JsonSerializable(typeof(UndoRecord))]
internal sealed partial class JournalJsonContext : JsonSerializerContext;
