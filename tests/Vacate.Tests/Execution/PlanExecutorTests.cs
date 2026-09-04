using Vacate.Abstractions.Execution;
using Vacate.Abstractions.Model;
using Vacate.Abstractions.Safety;
using Vacate.Core.Execution;
using Vacate.Core.Safety;
using Xunit;

namespace Vacate.Tests.Execution;

/// <summary>
/// Проверки исполнителя планов.
/// </summary>
/// <remarks>
/// Главный тест здесь — доказательство неизменности при сухом прогоне, и он работает
/// на настоящих файлах во временном каталоге, а не на заглушках. Заглушка доказала бы
/// только то, что заглушка ничего не делает.
/// </remarks>
public sealed class PlanExecutorTests : IDisposable
{
    private readonly string _sandbox;

    public PlanExecutorTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "Vacate.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
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
            // Уборка после теста не должна ронять прогон.
        }
    }

    private string CreateFile(string name, string content = "тестовые данные")
    {
        var path = Path.Combine(_sandbox, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static PlanExecutor CreateExecutor(
        IEffectSink sink,
        InMemoryJournal journal,
        bool isDryRun,
        IEnumerable<IGroupGuard>? groupGuards = null,
        IEnumerable<IItemGuard>? itemGuards = null,
        IVolumeInfoProvider? volumes = null,
        IGuardEnvironmentProvider? environment = null)
        => new(
            sink,
            journal,
            volumes ?? new StubVolumeInfoProvider(),
            environment ?? new StubEnvironmentProvider(),
            groupGuards ?? [],
            itemGuards ?? [],
            isDryRun);

    [Fact]
    public async Task Сухой_прогон_не_удаляет_ни_одного_файла()
    {
        var files = new[] { CreateFile("a.tmp"), CreateFile("b.tmp"), CreateFile("c.tmp") };
        var plan = PlanBuilder.ForFiles(files);

        var sink = new RecordingEffectSink();
        var journal = new InMemoryJournal();
        var executor = CreateExecutor(sink, journal, isDryRun: true);

        var report = await executor.ExecuteAsync(plan, progress: null, CancellationToken.None);

        // Ни один файл не исчез — это и есть суть предпросмотра.
        Assert.All(files, path => Assert.True(File.Exists(path), $"Сухой прогон удалил {path}"));

        // При этом план разобран полностью и намерения записаны.
        Assert.Equal(3, sink.Recorded.Count);
        Assert.Equal(3, report.Succeeded);
        Assert.True(report.WasDryRun);
    }

    [Fact]
    public async Task Сухой_прогон_не_приписывает_себе_освобождённое_место()
    {
        var plan = PlanBuilder.ForFiles([CreateFile("a.tmp")]);
        var executor = CreateExecutor(new RecordingEffectSink(), new InMemoryJournal(), isDryRun: true);

        var report = await executor.ExecuteAsync(plan, null, CancellationToken.None);

        // Показывать оценку как факт означало бы делать ровно то,
        // за что продукт критикует конкурентов.
        Assert.Equal(0, report.ActuallyFreedBytes);
    }

    [Fact]
    public async Task Реальное_выполнение_действительно_удаляет_файлы()
    {
        // Контрольный тест: если бы он не проходил, предыдущие ничего не доказывали.
        var files = new[] { CreateFile("a.tmp"), CreateFile("b.tmp") };
        var plan = PlanBuilder.ForFiles(files);

        var executor = CreateExecutor(new RealFileDeletingSink(), new InMemoryJournal(), isDryRun: false);

        var report = await executor.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.All(files, path => Assert.False(File.Exists(path)));
        Assert.Equal(2, report.Succeeded);
    }

    [Fact]
    public async Task Охрана_не_пропускает_группу_с_защищённым_путём()
    {
        var policy = PathPolicy.CreateDefault(@"C:\Windows", @"C:\", []);
        var plan = PlanBuilder.ForFiles([@"C:\Windows\System32\drivers\etc\hosts"]);

        var sink = new RecordingEffectSink();
        var executor = CreateExecutor(sink, new InMemoryJournal(), isDryRun: false,
            groupGuards: [new ProtectedPathGuard(policy)]);

        var report = await executor.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Equal(1, report.Denied);
        Assert.Equal(0, report.Succeeded);
        Assert.Empty(sink.Recorded);
    }

    [Fact]
    public async Task Смешивание_очистки_Корзины_с_удалением_в_Корзину_запрещено()
    {
        // Иначе очистка сначала отправит файлы в Корзину, потом очистит её,
        // и кнопка отката останется активной, но работать не будет.
        var operations = new List<PlannedOperation>
        {
            new DeleteFileOperation
            {
                Id = "op-1",
                GroupId = "group-1",
                DeclaredRisk = RiskLevel.Green,
                Consequence = LocalizedText.FromResource("Test.Consequence"),
                Target = new FileTarget(CreateFile("doc.txt"), false, 1024, FileTraits.None),
                Disposition = DeleteDisposition.RecycleBin,
            },
            new EmptyRecycleBinOperation
            {
                Id = "op-2",
                GroupId = "group-1",
                DeclaredRisk = RiskLevel.Yellow,
                Consequence = LocalizedText.FromResource("Test.Consequence"),
                VolumeRoot = @"C:\",
            },
        };

        var executor = CreateExecutor(new RecordingEffectSink(), new InMemoryJournal(), isDryRun: false,
            groupGuards: [new RecycleBinOrderGuard()]);

        var report = await executor.ExecuteAsync(PlanBuilder.Wrap(operations), null, CancellationToken.None);

        Assert.Equal(2, report.Denied);
    }

    [Fact]
    public async Task Точки_повторной_обработки_не_удаляются()
    {
        // Соединение каталогов даёт «два одинаковых файла по разным путям».
        // Удаление такого объекта уничтожает единственную копию данных.
        var operations = new List<PlannedOperation>
        {
            new DeleteFileOperation
            {
                Id = "op-1",
                GroupId = "group-1",
                DeclaredRisk = RiskLevel.Yellow,
                Consequence = LocalizedText.FromResource("Test.Consequence"),
                Target = new FileTarget(@"C:\Users\Test\Documents", true, 0, FileTraits.ReparsePoint),
                Disposition = DeleteDisposition.Quarantine,
            },
        };

        var executor = CreateExecutor(new RecordingEffectSink(), new InMemoryJournal(), isDryRun: false,
            itemGuards: [new ReparseAndCloudGuard()]);

        var report = await executor.ExecuteAsync(PlanBuilder.Wrap(operations), null, CancellationToken.None);

        Assert.Equal(1, report.Denied);
    }

    [Fact]
    public async Task Облачные_заглушки_не_трогаются()
    {
        // Открытие заглушки заставит систему скачать файл: программа израсходует
        // трафик пользователя и займёт место вместо того, чтобы освободить.
        var operations = new List<PlannedOperation>
        {
            new DeleteFileOperation
            {
                Id = "op-1",
                GroupId = "group-1",
                DeclaredRisk = RiskLevel.Yellow,
                Consequence = LocalizedText.FromResource("Test.Consequence"),
                Target = new FileTarget(@"C:\Users\Test\OneDrive\photo.jpg", false, 5_000_000, FileTraits.CloudPlaceholder),
                Disposition = DeleteDisposition.Quarantine,
            },
        };

        var executor = CreateExecutor(new RecordingEffectSink(), new InMemoryJournal(), isDryRun: false,
            itemGuards: [new ReparseAndCloudGuard()]);

        var report = await executor.ExecuteAsync(PlanBuilder.Wrap(operations), null, CancellationToken.None);

        Assert.Equal(1, report.Denied);
    }

    [Fact]
    public async Task В_аварийном_режиме_карантин_недоступен_и_операция_отклоняется()
    {
        var plan = PlanBuilder.ForFiles([CreateFile("a.tmp")], DeleteDisposition.Quarantine);

        var executor = CreateExecutor(new RecordingEffectSink(), new InMemoryJournal(), isDryRun: false,
            groupGuards: [new EmergencyModeGuard()],
            environment: new StubEnvironmentProvider(emergency: true));

        var report = await executor.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Equal(1, report.Denied);
    }

    [Fact]
    public async Task В_аварийном_режиме_безвозвратная_очистка_разрешена_с_повышенным_риском()
    {
        // Иначе продукт отказывается работать ровно тогда, когда он нужен.
        var files = new[] { CreateFile("a.tmp") };
        var plan = PlanBuilder.ForFiles(files, DeleteDisposition.Permanent);

        var executor = CreateExecutor(new RealFileDeletingSink(), new InMemoryJournal(), isDryRun: false,
            groupGuards: [new EmergencyModeGuard()],
            environment: new StubEnvironmentProvider(emergency: true));

        var report = await executor.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Equal(1, report.Succeeded);
        Assert.All(files, path => Assert.False(File.Exists(path)));
    }

    [Fact]
    public async Task Большой_объём_поднимает_риск_но_не_запрещает_операцию()
    {
        var operations = new List<PlannedOperation>
        {
            new DeleteFileOperation
            {
                Id = "op-1",
                GroupId = "group-1",
                DeclaredRisk = RiskLevel.Green,
                Consequence = LocalizedText.FromResource("Test.Consequence"),
                Target = new FileTarget(CreateFile("huge.bin"), false, 30L * 1024 * 1024 * 1024, FileTraits.None),
                Disposition = DeleteDisposition.Permanent,
            },
        };

        var executor = CreateExecutor(new RealFileDeletingSink(), new InMemoryJournal(), isDryRun: false,
            groupGuards: [new VolumeLimitGuard()]);

        var report = await executor.ExecuteAsync(PlanBuilder.Wrap(operations), null, CancellationToken.None);

        Assert.Equal(0, report.Denied);
        Assert.Equal(1, report.Succeeded);
    }

    [Fact]
    public async Task Честный_счётчик_берёт_разницу_свободного_места_а_не_сумму_размеров()
    {
        // Заявлено к удалению 3 КБ, а свободного места прибавилось только 1 КБ:
        // отчёт обязан показать вторую цифру, а не первую.
        var files = new[] { CreateFile("a.tmp"), CreateFile("b.tmp"), CreateFile("c.tmp") };
        var plan = PlanBuilder.ForFiles(files);

        var volumes = new StubVolumeInfoProvider(freeBefore: 1_000_000, freeAfter: 1_001_024);
        var executor = CreateExecutor(new RealFileDeletingSink(), new InMemoryJournal(), isDryRun: false, volumes: volumes);

        var report = await executor.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Equal(3072, report.ClaimedBytes);
        Assert.Equal(1024, report.ActuallyFreedBytes);
    }

    [Fact]
    public async Task Занятый_файл_пропускается_и_попадает_в_объяснение_расхождения()
    {
        var path = CreateFile("locked.tmp");
        var plan = PlanBuilder.ForFiles([path]);

        // Держим файл открытым — так же, как это делает работающая программа.
        using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var executor = CreateExecutor(new RealFileDeletingSink(), new InMemoryJournal(), isDryRun: false);
        var report = await executor.ExecuteAsync(plan, null, CancellationToken.None);

        Assert.Equal(1, report.Failed);
        Assert.True(File.Exists(path));
        Assert.Contains(report.Discrepancies, d => d.Kind == DiscrepancyKind.HeldByProcess);
    }

    [Fact]
    public async Task Прерывание_не_теряет_уже_сделанную_работу()
    {
        var files = Enumerable.Range(0, 50).Select(i => CreateFile($"f{i}.tmp")).ToArray();
        var plan = PlanBuilder.ForFiles(files);

        using var cts = new CancellationTokenSource();
        var journal = new InMemoryJournal();

        var executor = CreateExecutor(
            new CancellingSink(cts, cancelAfter: 10),
            journal,
            isDryRun: false);

        var report = await executor.ExecuteAsync(plan, null, cts.Token);

        Assert.True(report.Cancelled);
        Assert.True(report.Succeeded > 0, "Работа, выполненная до отмены, должна быть учтена");
        Assert.NotNull(journal.Completed);
    }

    /// <summary>Приёмник, отменяющий операцию после заданного числа действий.</summary>
    private sealed class CancellingSink(CancellationTokenSource cts, int cancelAfter) : IEffectSink
    {
        private int _count;

        public Task<EffectOutcome> DeleteFileAsync(FileTarget target, DeleteDisposition disposition, CancellationToken ct)
        {
            if (++_count >= cancelAfter)
            {
                cts.Cancel();
            }

            return Task.FromResult(EffectOutcome.Success(target.SizeOnDiskBytes));
        }

        public Task<EffectOutcome> DeleteRegistryAsync(RegistryTarget target, CancellationToken ct)
            => Task.FromResult(EffectOutcome.Success(0));

        public Task<EffectOutcome> SetRegistryValueAsync(RegistryTarget target, RegistryValueData value, CancellationToken ct)
            => Task.FromResult(EffectOutcome.Success(0));

        public Task<EffectOutcome> EmptyRecycleBinAsync(string volumeRoot, CancellationToken ct)
            => Task.FromResult(EffectOutcome.Success(0));
    }
}
