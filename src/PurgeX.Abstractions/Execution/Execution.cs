using PurgeX.Abstractions.Model;

namespace PurgeX.Abstractions.Execution;

/// <summary>
/// Единственный вход для выполнения изменений. Прикладной код не имеет другого способа
/// что-либо удалить: подходящих методов просто нет в его области видимости.
/// </summary>
public interface IPlanExecutor
{
    /// <summary>
    /// Выполнить план целиком. Охрана применяется внутри, обойти её нельзя.
    /// </summary>
    /// <remarks>
    /// Один и тот же метод используется и для предпросмотра, и для реального выполнения.
    /// Разница только в том, какая реализация <see cref="IEffectSink"/> подставлена
    /// при сборке зависимостей. Отдельной ветки кода для предпросмотра нет намеренно:
    /// именно из-за неё в других продуктах предпросмотр со временем расходится с реальностью.
    /// </remarks>
    Task<ExecutionReport> ExecuteAsync(
        MutationPlan plan,
        IProgress<ExecutionProgress>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Приёмник действий: то, что фактически меняет систему.
/// </summary>
/// <remarks>
/// ИНФРАСТРУКТУРНЫЙ контракт. Реализуется только платформенным слоем и подставляется
/// только при сборке зависимостей. Прикладной код обращается к <see cref="IPlanExecutor"/>.
///
/// Две реализации:
///   - боевая, выполняющая операции;
///   - записывающая, которая ничего не делает и только фиксирует намерения (сухой прогон).
/// </remarks>
public interface IEffectSink
{
    Task<EffectOutcome> DeleteFileAsync(FileTarget target, DeleteDisposition disposition, CancellationToken ct);

    Task<EffectOutcome> DeleteRegistryAsync(RegistryTarget target, CancellationToken ct);

    Task<EffectOutcome> SetRegistryValueAsync(RegistryTarget target, RegistryValueData value, CancellationToken ct);

    Task<EffectOutcome> EmptyRecycleBinAsync(string volumeRoot, CancellationToken ct);
}

/// <summary>Результат одного действия.</summary>
/// <param name="Status">Чем закончилось.</param>
/// <param name="FreedOnDiskBytes">Сколько места фактически освободила эта операция.</param>
/// <param name="UndoToken">
/// Как вернуть сделанное. Пустая ссылка означает, что операция необратима,
/// и это должно быть честно сказано пользователю ещё до выполнения.
/// </param>
/// <param name="FailureReason">Причина отказа человеческим языком, а не код ошибки.</param>
/// <param name="HoldingProcess">Кто держит объект, если он занят. Имя процесса, не «доступ запрещён».</param>
public sealed record EffectOutcome(
    EffectStatus Status,
    long FreedOnDiskBytes = 0,
    string? UndoToken = null,
    LocalizedText? FailureReason = null,
    string? HoldingProcess = null)
{
    public static EffectOutcome Success(long freed, string? undoToken = null)
        => new(EffectStatus.Succeeded, freed, undoToken);

    public static EffectOutcome Skipped(LocalizedText reason)
        => new(EffectStatus.Skipped, 0, null, reason);

    public static EffectOutcome Failed(LocalizedText reason, string? holder = null)
        => new(EffectStatus.Failed, 0, null, reason, holder);
}

public enum EffectStatus
{
    Succeeded,

    /// <summary>
    /// Пропущено намеренно: объект исчез сам, оказался занят, не прошёл повторную проверку.
    /// Пропуск одного объекта не срывает весь пакет.
    /// </summary>
    Skipped,

    Failed,
}

/// <summary>Итог выполнения плана.</summary>
public sealed record ExecutionReport
{
    public required string PlanId { get; init; }
    public required string SessionId { get; init; }

    public required int Succeeded { get; init; }
    public required int Skipped { get; init; }
    public required int Failed { get; init; }
    public required int Denied { get; init; }

    /// <summary>Сумма размеров объектов, с которыми мы работали.</summary>
    public required long ClaimedBytes { get; init; }

    /// <summary>
    /// Насколько фактически изменилось свободное место на дисках.
    /// Меряется независимо от суммы размеров — в этом весь смысл честного счётчика:
    /// конкуренты складывают размеры файлов и никогда не сверяются с реальностью.
    /// </summary>
    public required long ActuallyFreedBytes { get; init; }

    /// <summary>Из чего сложилось расхождение между заявленным и фактическим.</summary>
    public required IReadOnlyList<DiscrepancyReason> Discrepancies { get; init; }

    /// <summary>Операция была прервана пользователем или системой.</summary>
    public bool Cancelled { get; init; }

    /// <summary>Ничего не менялось: это был предпросмотр.</summary>
    public bool WasDryRun { get; init; }
}

/// <summary>
/// Объяснение, почему освободилось не столько, сколько удалили.
/// </summary>
/// <param name="Kind">Причина.</param>
/// <param name="Bytes">Сколько байт приходится на эту причину.</param>
/// <param name="Detail">Подробность: имя процесса, путь, число объектов.</param>
public sealed record DiscrepancyReason(DiscrepancyKind Kind, long Bytes, string? Detail = null);

public enum DiscrepancyKind
{
    /// <summary>Файл удалён, но его держит открытым другой процесс: место вернётся при закрытии.</summary>
    HeldByProcess,

    /// <summary>Не удалось удалить: отказ в доступе, блокировка.</summary>
    NotDeleted,

    /// <summary>У файла несколько жёстких ссылок: он числится дважды, а занимает место один раз.</summary>
    HardLinked,

    /// <summary>Логический размер больше занимаемого места: сжатие или разреженный файл.</summary>
    CompressedOrSparse,

    /// <summary>Объект перемещён в карантин и лежит на том же томе. Место вернётся после его истечения.</summary>
    InQuarantine,

    /// <summary>Объект перемещён в Корзину. Место вернётся после её очистки.</summary>
    InRecycleBin,
}

/// <summary>Ход выполнения. Обновления прореживаются, чтобы не захлебнулся интерфейс.</summary>
/// <param name="ProcessedCount">Обработано операций.</param>
/// <param name="TotalCount">Всего операций.</param>
/// <param name="CurrentPath">Что обрабатывается прямо сейчас.</param>
/// <param name="FreedSoFarBytes">Освобождено на текущий момент.</param>
public sealed record ExecutionProgress(int ProcessedCount, int TotalCount, string? CurrentPath, long FreedSoFarBytes);
