using PurgeX.Abstractions.Model;

namespace PurgeX.Abstractions.Safety;

/// <summary>
/// Журнал операций: что программа сделала и как это отменить.
/// </summary>
/// <remarks>
/// Запись ведётся ГРУППАМИ, а не по каждому файлу. Очистка на двести тысяч объектов
/// при поэлементной записи дала бы сотни тысяч строк и часы работы с диском.
/// Поэлементно хранится только то, что действительно можно вернуть — то есть попавшее
/// в карантин или имеющее сохранённое прежнее состояние.
/// </remarks>
public interface IOperationJournal
{
    /// <summary>Начать сессию. Одна сессия — один запуск очистки.</summary>
    Task<string> BeginSessionAsync(string origin, CancellationToken ct);

    /// <summary>Записать итог по группе с агрегатами.</summary>
    Task RecordGroupAsync(string sessionId, GroupJournalEntry entry, CancellationToken ct);

    /// <summary>Записать объект, который можно вернуть.</summary>
    Task RecordUndoableAsync(string sessionId, UndoableEntry entry, CancellationToken ct);

    /// <summary>Завершить сессию с итогом.</summary>
    Task CompleteSessionAsync(string sessionId, SessionSummary summary, CancellationToken ct);

    /// <summary>Сессии для раздела истории, свежие первыми.</summary>
    Task<IReadOnlyList<SessionSummary>> GetRecentSessionsAsync(int limit, CancellationToken ct);

    /// <summary>Всё, что можно вернуть в рамках сессии.</summary>
    Task<IReadOnlyList<UndoableEntry>> GetUndoableAsync(string sessionId, CancellationToken ct);

    /// <summary>
    /// Отметить объект как возвращённый. Записи не удаляются: история должна
    /// показывать и то, что было откачено.
    /// </summary>
    Task MarkRestoredAsync(string sessionId, string undoToken, CancellationToken ct);
}

/// <param name="GroupId">Идентификатор группы.</param>
/// <param name="TitleKey">Ключ названия для показа.</param>
/// <param name="ItemCount">Сколько объектов обработано.</param>
/// <param name="ClaimedBytes">Суммарный размер обработанного.</param>
/// <param name="Succeeded">Успешно.</param>
/// <param name="Skipped">Пропущено.</param>
/// <param name="Failed">С ошибкой.</param>
public sealed record GroupJournalEntry(
    string GroupId,
    string TitleKey,
    int ItemCount,
    long ClaimedBytes,
    int Succeeded,
    int Skipped,
    int Failed);

/// <summary>Объект, который можно вернуть.</summary>
/// <param name="UndoToken">Идентификатор для возврата.</param>
/// <param name="OriginalPath">Откуда взят.</param>
/// <param name="Kind">Что это было.</param>
/// <param name="SizeOnDiskBytes">Занимаемое место.</param>
/// <param name="ExpiresAtUtc">Когда возможность возврата исчезнет.</param>
public sealed record UndoableEntry(
    string UndoToken,
    string OriginalPath,
    UndoableKind Kind,
    long SizeOnDiskBytes,
    DateTime ExpiresAtUtc);

public enum UndoableKind
{
    QuarantinedFile,
    RegistrySnapshot,
}

/// <summary>Итог сессии для раздела истории.</summary>
public sealed record SessionSummary
{
    public required string SessionId { get; init; }
    public required string Origin { get; init; }
    public required DateTime StartedAtUtc { get; init; }
    public DateTime? FinishedAtUtc { get; init; }

    public required long ClaimedBytes { get; init; }
    public required long ActuallyFreedBytes { get; init; }
    public required int ItemCount { get; init; }

    /// <summary>
    /// Возможность отката проверяется по факту наличия сохранённых данных,
    /// а не по тому, что записано в журнале: карантин мог истечь, а файлы —
    /// быть удалёнными антивирусом.
    /// </summary>
    public required bool HasRestorableItems { get; init; }

    public bool WasEmergencyMode { get; init; }
}
