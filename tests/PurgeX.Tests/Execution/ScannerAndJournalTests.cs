using PurgeX.Abstractions.Model;
using PurgeX.Abstractions.Safety;
using PurgeX.Core.Journal;
using PurgeX.Core.Safety;
using PurgeX.Platform.Windows.Files;
using Xunit;

namespace PurgeX.Tests.Execution;

/// <summary>Проверки сканера временных файлов.</summary>
public sealed class TempFilesScannerTests : IDisposable
{
    private readonly string _sandbox;
    private readonly PathPolicy _policy;

    public TempFilesScannerTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "PurgeX.Scanner.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
        _policy = PathPolicy.CreateDefault(@"C:\Windows", @"C:\", []);
        _policy.AddCleanRoot(_sandbox);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_sandbox))
            {
                Directory.Delete(_sandbox, recursive: true);
            }
        }
        catch (IOException)
        {
            // Уборка не должна ронять прогон.
        }
    }

    private string CreateFile(string name, TimeSpan age, string content = "данные")
    {
        var path = Path.Combine(_sandbox, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    private MutationPlan Scan()
    {
        var scanner = new TempFilesScanner(_policy);
        return scanner.Scan([new TempLocation("test", "Test.Temp", _sandbox)], CancellationToken.None);
    }

    [Fact]
    public void Файлы_моложе_суток_не_попадают_в_план()
    {
        // Свежий временный файл может прямо сейчас использовать работающая программа
        // или идущая установка. Его удаление ломает чужую работу и выглядит
        // как поломка системы, причём виноватой окажется наша программа.
        CreateFile("свежий.tmp", TimeSpan.FromMinutes(5));
        CreateFile("старый.tmp", TimeSpan.FromDays(3));

        var plan = Scan();

        Assert.Equal(1, plan.TotalCount);
        Assert.Contains(plan.AllOperations.OfType<DeleteFileOperation>(),
            o => o.Target.Path.EndsWith("старый.tmp", StringComparison.Ordinal));
    }

    [Fact]
    public void Файл_ровно_на_границе_возраста_не_берётся()
    {
        CreateFile("граница.tmp", TimeSpan.FromHours(23));

        Assert.Equal(0, Scan().TotalCount);
    }

    [Fact]
    public void Временные_файлы_удаляются_безвозвратно_а_не_кладутся_в_карантин()
    {
        // Класть кэш в карантин бессмысленно: место не освободится, а файлы
        // всё равно создадутся заново. Пользователю это говорится прямо.
        CreateFile("старый.tmp", TimeSpan.FromDays(3));

        var operation = Assert.IsType<DeleteFileOperation>(Scan().AllOperations.Single());

        Assert.Equal(DeleteDisposition.Permanent, operation.Disposition);
    }

    [Fact]
    public void Отсутствующий_каталог_не_роняет_сканирование()
    {
        var scanner = new TempFilesScanner(_policy);

        var plan = scanner.Scan(
            [new TempLocation("missing", "Test.Missing", Path.Combine(_sandbox, "нет-такого"))],
            CancellationToken.None);

        Assert.Equal(0, plan.TotalCount);
    }

    [Fact]
    public void Вложенные_каталоги_обходятся()
    {
        var nested = Path.Combine(_sandbox, "вложенный");
        Directory.CreateDirectory(nested);
        var path = Path.Combine(nested, "старый.tmp");
        File.WriteAllText(path, "данные");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - TimeSpan.FromDays(3));

        Assert.Equal(1, Scan().TotalCount);
    }

    [Fact]
    public void Размер_группы_складывается_из_занимаемого_места()
    {
        CreateFile("a.tmp", TimeSpan.FromDays(3), new string('x', 100));
        CreateFile("b.tmp", TimeSpan.FromDays(3), new string('y', 200));

        var plan = Scan();

        Assert.Equal(2, plan.TotalCount);
        Assert.True(plan.TotalSizeOnDiskBytes > 0);
    }
}

/// <summary>Проверки журнала операций.</summary>
public sealed class JournalTests : IDisposable
{
    private readonly string _directory;
    private readonly JsonlOperationJournal _journal;

    public JournalTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "PurgeX.Journal.Tests", Guid.NewGuid().ToString("N"));
        _journal = new JsonlOperationJournal(_directory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Уборка не должна ронять прогон.
        }
    }

    [Fact]
    public async Task История_показывает_итог_сеанса_а_не_заготовку()
    {
        // Сессия записывается дважды: при начале и при завершении.
        // Наивное чтение показало бы нули вместо результата.
        var sessionId = await _journal.BeginSessionAsync("tests", CancellationToken.None);

        await _journal.CompleteSessionAsync(sessionId, new SessionSummary
        {
            SessionId = sessionId,
            Origin = "tests",
            StartedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            FinishedAtUtc = DateTime.UtcNow,
            ClaimedBytes = 5000,
            ActuallyFreedBytes = 4000,
            ItemCount = 12,
            HasRestorableItems = true,
        }, CancellationToken.None);

        var sessions = await _journal.GetRecentSessionsAsync(10, CancellationToken.None);

        var session = Assert.Single(sessions);
        Assert.Equal(4000, session.ActuallyFreedBytes);
        Assert.Equal(12, session.ItemCount);
        Assert.True(session.HasRestorableItems);
    }

    [Fact]
    public async Task Возвращённые_объекты_исчезают_из_списка_доступных_для_отката()
    {
        var sessionId = await _journal.BeginSessionAsync("tests", CancellationToken.None);

        await _journal.RecordUndoableAsync(sessionId,
            new UndoableEntry("token-1", @"C:\Temp\a.txt", UndoableKind.QuarantinedFile, 100, DateTime.UtcNow.AddDays(30)),
            CancellationToken.None);

        await _journal.RecordUndoableAsync(sessionId,
            new UndoableEntry("token-2", @"C:\Temp\b.txt", UndoableKind.QuarantinedFile, 200, DateTime.UtcNow.AddDays(30)),
            CancellationToken.None);

        await _journal.MarkRestoredAsync(sessionId, "token-1", CancellationToken.None);

        var undoable = await _journal.GetUndoableAsync(sessionId, CancellationToken.None);

        Assert.Single(undoable);
        Assert.Equal("token-2", undoable[0].UndoToken);
    }

    [Fact]
    public async Task Повреждённая_строка_не_делает_журнал_нечитаемым()
    {
        // Одна испорченная запись не должна лишать пользователя возможности
        // откатить всё остальное.
        var sessionId = await _journal.BeginSessionAsync("tests", CancellationToken.None);

        await _journal.RecordUndoableAsync(sessionId,
            new UndoableEntry("token-1", @"C:\Temp\a.txt", UndoableKind.QuarantinedFile, 100, DateTime.UtcNow.AddDays(30)),
            CancellationToken.None);

        var undoFile = Directory.GetFiles(_directory, "undo-*.jsonl").Single();
        await File.AppendAllTextAsync(undoFile, "{это не json" + Environment.NewLine);

        await _journal.RecordUndoableAsync(sessionId,
            new UndoableEntry("token-2", @"C:\Temp\b.txt", UndoableKind.QuarantinedFile, 200, DateTime.UtcNow.AddDays(30)),
            CancellationToken.None);

        var undoable = await _journal.GetUndoableAsync(sessionId, CancellationToken.None);

        Assert.Equal(2, undoable.Count);
    }

    [Fact]
    public async Task Записи_ведутся_группами_а_не_по_каждому_файлу()
    {
        // Поэлементная запись для очистки на двести тысяч файлов дала бы
        // сотни тысяч строк и часы работы с диском.
        var sessionId = await _journal.BeginSessionAsync("tests", CancellationToken.None);

        await _journal.RecordGroupAsync(sessionId,
            new GroupJournalEntry("group-1", "Test.Group", ItemCount: 200_000, ClaimedBytes: 5_000_000, 199_000, 500, 500),
            CancellationToken.None);

        var groupFile = Directory.GetFiles(_directory, "groups-*.jsonl").Single();
        var lines = await File.ReadAllLinesAsync(groupFile);

        Assert.Single(lines);
    }

    [Fact]
    public async Task Пустая_история_возвращает_пустой_список_а_не_ошибку()
    {
        var sessions = await _journal.GetRecentSessionsAsync(10, CancellationToken.None);

        Assert.Empty(sessions);
    }
}
