using Vacate.Abstractions.Execution;
using Vacate.Abstractions.Model;
using Vacate.Abstractions.Safety;

namespace Vacate.Tests.Execution;

/// <summary>Журнал, хранящий записи в памяти.</summary>
internal sealed class InMemoryJournal : IOperationJournal
{
    public List<GroupJournalEntry> Groups { get; } = [];
    public List<UndoableEntry> Undoable { get; } = [];
    public SessionSummary? Completed { get; private set; }

    public Task<string> BeginSessionAsync(string origin, CancellationToken ct)
        => Task.FromResult("session-1");

    public Task RecordGroupAsync(string sessionId, GroupJournalEntry entry, CancellationToken ct)
    {
        Groups.Add(entry);
        return Task.CompletedTask;
    }

    public Task RecordUndoableAsync(string sessionId, UndoableEntry entry, CancellationToken ct)
    {
        Undoable.Add(entry);
        return Task.CompletedTask;
    }

    public Task CompleteSessionAsync(string sessionId, SessionSummary summary, CancellationToken ct)
    {
        Completed = summary;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SessionSummary>> GetRecentSessionsAsync(int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SessionSummary>>(Completed is null ? [] : [Completed]);

    public Task<IReadOnlyList<UndoableEntry>> GetUndoableAsync(string sessionId, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<UndoableEntry>>(Undoable);

    public Task MarkRestoredAsync(string sessionId, string undoToken, CancellationToken ct)
        => Task.CompletedTask;
}

/// <summary>Сведения о томах с заданными вручную значениями.</summary>
internal sealed class StubVolumeInfoProvider(long freeBefore = 1_000_000, long freeAfter = 1_000_000) : IVolumeInfoProvider
{
    private bool _firstCallDone;

    public IReadOnlyDictionary<string, long> GetFreeSpaceByVolume()
    {
        var value = _firstCallDone ? freeAfter : freeBefore;
        _firstCallDone = true;
        return new Dictionary<string, long> { [@"C:\"] = value };
    }

    public string GetVolumeRoot(string path) => @"C:\";
}

/// <summary>Окружение охраны с заданными параметрами.</summary>
internal sealed class StubEnvironmentProvider(bool emergency = false, bool advanced = false) : IGuardEnvironmentProvider
{
    public GuardEnvironment Create() => new(
        TargetUserSid: "S-1-5-21-TEST",
        TargetUserProfilePath: @"C:\Users\Test",
        FreeSpaceByVolume: new Dictionary<string, long> { [@"C:\"] = 1_000_000 },
        IsEmergencyMode: emergency,
        AdvancedMode: advanced);
}

/// <summary>
/// Приёмник, который действительно удаляет файлы. Нужен, чтобы отличить
/// настоящее выполнение от предпросмотра в тестах.
/// </summary>
internal sealed class RealFileDeletingSink : IEffectSink
{
    public Task<EffectOutcome> DeleteFileAsync(FileTarget target, DeleteDisposition disposition, CancellationToken ct)
    {
        try
        {
            if (File.Exists(target.Path))
            {
                File.Delete(target.Path);
                return Task.FromResult(EffectOutcome.Success(target.SizeOnDiskBytes));
            }

            return Task.FromResult(EffectOutcome.Skipped(LocalizedText.FromResource("Test.Missing")));
        }
        catch (IOException)
        {
            return Task.FromResult(EffectOutcome.Failed(LocalizedText.FromResource("Test.Locked"), "test-process"));
        }
    }

    public Task<EffectOutcome> DeleteRegistryAsync(RegistryTarget target, CancellationToken ct)
        => Task.FromResult(EffectOutcome.Success(0));

    public Task<EffectOutcome> SetRegistryValueAsync(RegistryTarget target, RegistryValueData value, CancellationToken ct)
        => Task.FromResult(EffectOutcome.Success(0));

    public Task<EffectOutcome> EmptyRecycleBinAsync(string volumeRoot, CancellationToken ct)
        => Task.FromResult(EffectOutcome.Success(0));
}

/// <summary>Вспомогательные построители планов.</summary>
internal static class PlanBuilder
{
    public static MutationPlan ForFiles(IEnumerable<string> paths, DeleteDisposition disposition = DeleteDisposition.Permanent, RiskLevel risk = RiskLevel.Green)
    {
        var operations = paths.Select((path, index) => (PlannedOperation)new DeleteFileOperation
        {
            Id = $"op-{index}",
            GroupId = "group-1",
            DeclaredRisk = risk,
            Consequence = LocalizedText.FromResource("Test.Consequence"),
            Target = new FileTarget(path, IsDirectory: false, SizeOnDiskBytes: 1024, Traits: FileTraits.None),
            Disposition = disposition,
        }).ToList();

        return Wrap(operations);
    }

    public static MutationPlan Wrap(IReadOnlyList<PlannedOperation> operations, string? rootPath = null) => new()
    {
        PlanId = "plan-1",
        Origin = "tests",
        Groups =
        [
            new OperationGroup
            {
                GroupId = "group-1",
                Title = LocalizedText.FromResource("Test.Group"),
                RootPath = rootPath,
                Operations = operations,
                SizeOnDiskBytes = operations.OfType<DeleteFileOperation>().Sum(o => o.Target.SizeOnDiskBytes),
            },
        ],
    };
}
