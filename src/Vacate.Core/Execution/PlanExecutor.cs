using System.Diagnostics;
using Vacate.Abstractions.Execution;
using Vacate.Abstractions.Model;
using Vacate.Abstractions.Safety;

namespace Vacate.Core.Execution;

/// <summary>
/// Исполнитель планов: единственное место в продукте, где происходят изменения.
/// </summary>
/// <remarks>
/// Порядок работы намеренно такой:
///
///   1. Дешёвые проверки применяются ОДИН РАЗ на группу. Прогонять десяток тяжёлых проверок
///      на каждом из сотен тысяч файлов — это часы работы, после которых охрану отключат
///      «чтобы наконец заработало».
///   2. Дорогие поэлементные проверки применяются только к жёлтым и красным операциям.
///   3. Отказ на одном объекте пропускает объект, но не срывает весь пакет.
///   4. Свободное место замеряется до и после — это и есть честный счётчик.
/// </remarks>
public sealed class PlanExecutor(
    IEffectSink sink,
    IOperationJournal journal,
    IVolumeInfoProvider volumes,
    IGuardEnvironmentProvider environmentProvider,
    IEnumerable<IGroupGuard> groupGuards,
    IEnumerable<IItemGuard> itemGuards,
    bool isDryRun) : IPlanExecutor
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);

    private readonly IGroupGuard[] _groupGuards = groupGuards.OrderBy(g => g.Order).ToArray();
    private readonly IItemGuard[] _itemGuards = itemGuards.OrderBy(g => g.Order).ToArray();

    public async Task<ExecutionReport> ExecuteAsync(
        MutationPlan plan,
        IProgress<ExecutionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var environment = environmentProvider.Create();
        var sessionId = await journal.BeginSessionAsync(plan.Origin, cancellationToken).ConfigureAwait(false);

        var freeBefore = volumes.GetFreeSpaceByVolume();

        var counters = new Counters();
        var heldByProcess = new List<(string Path, long Bytes, string Process)>();
        var notDeleted = 0L;
        var quarantined = 0L;
        var recycled = 0L;

        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        var cancelled = false;

        try
        {
            foreach (var group in plan.Groups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var groupResult = EvaluateGroup(group, environment);

                if (groupResult.Denied)
                {
                    counters.Denied += group.Operations.Count;
                    await RecordGroupAsync(sessionId, group, 0, 0, group.Operations.Count, cancellationToken)
                        .ConfigureAwait(false);
                    continue;
                }

                var groupSucceeded = 0;
                var groupSkipped = 0;
                var groupFailed = 0;

                // Дорогие проверки нужны только там, где цена ошибки высока.
                var needsItemGuards = groupResult.EffectiveRisk >= RiskLevel.Yellow;

                foreach (var operation in group.Operations)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (needsItemGuards && IsDeniedByItemGuards(operation, environment))
                    {
                        counters.Denied++;
                        continue;
                    }

                    var outcome = await ApplyAsync(operation, cancellationToken).ConfigureAwait(false);

                    switch (outcome.Status)
                    {
                        case EffectStatus.Succeeded:
                            counters.Succeeded++;
                            groupSucceeded++;
                            counters.ClaimedBytes += SizeOf(operation);

                            if (outcome.UndoToken is { } token)
                            {
                                await journal.RecordUndoableAsync(
                                    sessionId,
                                    new UndoableEntry(
                                        token,
                                        DescribeTarget(operation),
                                        operation is DeleteFileOperation ? UndoableKind.QuarantinedFile : UndoableKind.RegistrySnapshot,
                                        SizeOf(operation),
                                        DateTime.UtcNow.AddDays(30)),
                                    cancellationToken).ConfigureAwait(false);
                            }

                            if (operation is DeleteFileOperation delete)
                            {
                                switch (delete.Disposition)
                                {
                                    case DeleteDisposition.Quarantine:
                                        quarantined += delete.Target.SizeOnDiskBytes;
                                        break;
                                    case DeleteDisposition.RecycleBin:
                                        recycled += delete.Target.SizeOnDiskBytes;
                                        break;
                                }
                            }

                            break;

                        case EffectStatus.Skipped:
                            counters.Skipped++;
                            groupSkipped++;
                            break;

                        default:
                            counters.Failed++;
                            groupFailed++;
                            notDeleted += SizeOf(operation);

                            if (outcome.HoldingProcess is { } holder)
                            {
                                heldByProcess.Add((DescribeTarget(operation), SizeOf(operation), holder));
                            }

                            break;
                    }

                    if (progress is not null && stopwatch.Elapsed - lastReport >= ProgressInterval)
                    {
                        lastReport = stopwatch.Elapsed;
                        progress.Report(new ExecutionProgress(
                            counters.Processed,
                            plan.TotalCount,
                            DescribeTarget(operation),
                            counters.ClaimedBytes));
                    }
                }

                await RecordGroupAsync(sessionId, group, groupSucceeded, groupSkipped, groupFailed, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Прерывание — это не сбой. Всё, что уже сделано, записано в журнал
            // и может быть отменено обычным образом.
            cancelled = true;
        }

        var freeAfter = volumes.GetFreeSpaceByVolume();
        var actuallyFreed = CalculateFreed(freeBefore, freeAfter);

        var discrepancies = BuildDiscrepancies(heldByProcess, notDeleted, quarantined, recycled);

        var report = new ExecutionReport
        {
            PlanId = plan.PlanId,
            SessionId = sessionId,
            Succeeded = counters.Succeeded,
            Skipped = counters.Skipped,
            Failed = counters.Failed,
            Denied = counters.Denied,
            ClaimedBytes = counters.ClaimedBytes,
            // В предпросмотре ничего не менялось, поэтому и освобождать нечего:
            // показывать здесь оценку означало бы выдавать прогноз за факт.
            ActuallyFreedBytes = isDryRun ? 0 : actuallyFreed,
            Discrepancies = discrepancies,
            Cancelled = cancelled,
            WasDryRun = isDryRun,
        };

        await journal.CompleteSessionAsync(sessionId, new SessionSummary
        {
            SessionId = sessionId,
            Origin = plan.Origin,
            StartedAtUtc = DateTime.UtcNow - stopwatch.Elapsed,
            FinishedAtUtc = DateTime.UtcNow,
            ClaimedBytes = report.ClaimedBytes,
            ActuallyFreedBytes = report.ActuallyFreedBytes,
            ItemCount = counters.Processed,
            HasRestorableItems = quarantined > 0,
            WasEmergencyMode = environment.IsEmergencyMode,
        }, cancellationToken).ConfigureAwait(false);

        return report;
    }

    private GroupEvaluation EvaluateGroup(OperationGroup group, GuardEnvironment environment)
    {
        var risk = group.MaxDeclaredRisk;

        foreach (var guard in _groupGuards)
        {
            var verdict = guard.Evaluate(group, environment);

            if (verdict.Decision == GuardDecision.Deny)
            {
                return new GroupEvaluation(true, risk);
            }

            // Охрана может только повысить уровень риска, но никогда не понизить:
            // иначе одна ошибка в правиле очистки открывала бы дорогу опасной операции.
            if (verdict.RaiseRiskTo is { } raised && raised > risk)
            {
                risk = raised;
            }
        }

        return new GroupEvaluation(false, risk);
    }

    private bool IsDeniedByItemGuards(PlannedOperation operation, GuardEnvironment environment)
        => _itemGuards.Any(guard => guard.Evaluate(operation, environment).Decision == GuardDecision.Deny);

    private Task<EffectOutcome> ApplyAsync(PlannedOperation operation, CancellationToken ct) => operation switch
    {
        DeleteFileOperation op => sink.DeleteFileAsync(op.Target, op.Disposition, ct),
        DeleteRegistryOperation op => sink.DeleteRegistryAsync(op.Target, ct),
        SetRegistryValueOperation op => sink.SetRegistryValueAsync(op.Target, op.Value, ct),
        EmptyRecycleBinOperation op => sink.EmptyRecycleBinAsync(op.VolumeRoot, ct),
        _ => Task.FromResult(EffectOutcome.Skipped(LocalizedText.FromResource("Execution.UnknownOperation"))),
    };

    private Task RecordGroupAsync(
        string sessionId,
        OperationGroup group,
        int succeeded,
        int skipped,
        int failed,
        CancellationToken ct)
        => journal.RecordGroupAsync(
            sessionId,
            new GroupJournalEntry(
                group.GroupId,
                group.Title.ResourceKey ?? group.GroupId,
                group.Operations.Count,
                group.SizeOnDiskBytes,
                succeeded,
                skipped,
                failed),
            ct);

    private static long CalculateFreed(
        IReadOnlyDictionary<string, long> before,
        IReadOnlyDictionary<string, long> after)
    {
        long total = 0;

        foreach (var (volume, freeBefore) in before)
        {
            if (after.TryGetValue(volume, out var freeAfter))
            {
                var delta = freeAfter - freeBefore;

                // Отрицательная разница означает, что за время работы кто-то ещё писал на диск.
                // Приписывать себе чужую запись нельзя, поэтому такие значения отбрасываются.
                if (delta > 0)
                {
                    total += delta;
                }
            }
        }

        return total;
    }

    private static List<DiscrepancyReason> BuildDiscrepancies(
        List<(string Path, long Bytes, string Process)> heldByProcess,
        long notDeleted,
        long quarantined,
        long recycled)
    {
        var result = new List<DiscrepancyReason>();

        foreach (var group in heldByProcess.GroupBy(x => x.Process))
        {
            result.Add(new DiscrepancyReason(
                DiscrepancyKind.HeldByProcess,
                group.Sum(x => x.Bytes),
                group.Key));
        }

        if (notDeleted > 0)
        {
            result.Add(new DiscrepancyReason(DiscrepancyKind.NotDeleted, notDeleted));
        }

        if (quarantined > 0)
        {
            result.Add(new DiscrepancyReason(DiscrepancyKind.InQuarantine, quarantined));
        }

        if (recycled > 0)
        {
            result.Add(new DiscrepancyReason(DiscrepancyKind.InRecycleBin, recycled));
        }

        return result;
    }

    private static long SizeOf(PlannedOperation operation)
        => operation is DeleteFileOperation delete ? delete.Target.SizeOnDiskBytes : 0;

    private static string DescribeTarget(PlannedOperation operation) => operation switch
    {
        DeleteFileOperation op => op.Target.Path,
        DeleteRegistryOperation op => $"{op.Target.Hive}\\{op.Target.SubKeyPath}",
        SetRegistryValueOperation op => $"{op.Target.Hive}\\{op.Target.SubKeyPath}",
        EmptyRecycleBinOperation op => op.VolumeRoot,
        _ => operation.Id,
    };

    private readonly record struct GroupEvaluation(bool Denied, RiskLevel EffectiveRisk);

    private sealed class Counters
    {
        public int Succeeded;
        public int Skipped;
        public int Failed;
        public int Denied;
        public long ClaimedBytes;

        public int Processed => Succeeded + Skipped + Failed;
    }
}
