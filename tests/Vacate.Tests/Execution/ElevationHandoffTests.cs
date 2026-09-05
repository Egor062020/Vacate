using System.Text.Json;
using Vacate.Abstractions.Model;
using Vacate.Platform.Windows.Files;
using Xunit;

namespace Vacate.Tests.Execution;

/// <summary>
/// Передача плана между процессами.
/// </summary>
/// <remarks>
/// Интерфейс работает без прав администратора, а операции, которым права нужны,
/// уходят отдельному процессу через файл. Если план не переживает эту дорогу,
/// поднятый процесс получает пустоту и молча ничего не делает — а окно рапортует
/// об успехе. Поэтому дорога проверяется целиком, а не по частям.
/// </remarks>
public sealed class ElevationHandoffTests
{
    private static MutationPlan SamplePlan() => new()
    {
        PlanId = "plan-1",
        Origin = "test",
        Groups =
        [
            new OperationGroup
            {
                GroupId = "g1",
                Title = LocalizedText.FromResource("Clean.Temp.User"),
                RootPath = @"C:\Windows\Temp",
                SizeOnDiskBytes = 2048,
                Operations =
                [
                    new DeleteFileOperation
                    {
                        Id = "1",
                        GroupId = "g1",
                        DeclaredRisk = RiskLevel.Yellow,
                        Consequence = LocalizedText.FromResource("Clean.Temp.Consequence"),
                        Target = new FileTarget(@"C:\Windows\Temp\a.tmp", IsDirectory: false, 1024, FileTraits.ReadOnly),
                        Disposition = DeleteDisposition.Quarantine,
                    },
                    new DeleteRegistryOperation
                    {
                        Id = "2",
                        GroupId = "g1",
                        DeclaredRisk = RiskLevel.Red,
                        Consequence = LocalizedText.FromTranslations(new Dictionary<string, string>
                        {
                            ["ru"] = "Ветка будет удалена",
                            ["en"] = "The key will be deleted",
                        }),
                        Target = new RegistryTarget(RegistryHiveKind.LocalMachine, @"SOFTWARE\Zzqxwv", null, RegistryViewKind.Registry64),
                    },
                ],
            },
        ],
    };

    private static MutationPlan RoundTrip(MutationPlan plan)
    {
        var json = JsonSerializer.Serialize(plan);
        var restored = JsonSerializer.Deserialize<MutationPlan>(json);

        Assert.NotNull(restored);
        return restored;
    }

    [Fact]
    public void План_переживает_дорогу_между_процессами()
    {
        var restored = RoundTrip(SamplePlan());

        Assert.Equal("plan-1", restored.PlanId);
        Assert.Equal(2, restored.TotalCount);
        Assert.Equal(2048, restored.TotalSizeOnDiskBytes);
    }

    [Fact]
    public void Тип_каждой_операции_сохраняется()
    {
        // Дискриминаторы закреплены строками именно ради этого: если операция
        // приедет как базовый тип, поднятый процесс не поймёт, что делать.
        var operations = RoundTrip(SamplePlan()).AllOperations.ToList();

        Assert.IsType<DeleteFileOperation>(operations[0]);
        Assert.IsType<DeleteRegistryOperation>(operations[1]);
    }

    [Fact]
    public void Цель_файловой_операции_приезжает_целиком()
    {
        var file = RoundTrip(SamplePlan()).AllOperations.OfType<DeleteFileOperation>().Single();

        Assert.Equal(@"C:\Windows\Temp\a.tmp", file.Target.Path);
        Assert.Equal(1024, file.Target.SizeOnDiskBytes);
        Assert.Equal(DeleteDisposition.Quarantine, file.Disposition);

        // Признаки объекта влияют на решения охраны: потеря их означала бы,
        // что на той стороне проверки работают на других данных.
        Assert.True(file.Target.Traits.HasFlag(FileTraits.ReadOnly));
    }

    [Fact]
    public void Разрядность_представления_реестра_не_теряется()
    {
        // Потеря этого поля даёт классическую ошибку «удалили не ту ветку»:
        // 32-разрядные программы видят собственное представление того же пути.
        var registry = RoundTrip(SamplePlan()).AllOperations.OfType<DeleteRegistryOperation>().Single();

        Assert.Equal(RegistryViewKind.Registry64, registry.Target.View);
        Assert.Equal(RegistryHiveKind.LocalMachine, registry.Target.Hive);
        Assert.Equal(@"SOFTWARE\Zzqxwv", registry.Target.SubKeyPath);
    }

    [Fact]
    public void Объявленный_риск_не_понижается_в_дороге()
    {
        var operations = RoundTrip(SamplePlan()).AllOperations.ToList();

        Assert.Equal(RiskLevel.Yellow, operations[0].DeclaredRisk);
        Assert.Equal(RiskLevel.Red, operations[1].DeclaredRisk);
    }

    [Fact]
    public void Тексты_для_пользователя_переживают_дорогу()
    {
        // На той стороне отчёт пишется в журнал, и запись должна остаться
        // читаемой — в том числе после смены языка интерфейса.
        var operations = RoundTrip(SamplePlan()).AllOperations.ToList();

        Assert.Equal("Clean.Temp.Consequence", operations[0].Consequence.ResourceKey);
        Assert.Equal("Ветка будет удалена", operations[1].Consequence.Translations?["ru"]);
    }

    [Fact]
    public void Отчёт_поднятого_процесса_переживает_дорогу_обратно()
    {
        // Обратный путь важен не меньше: без этих цифр честный счётчик показал бы
        // ноль после каждой операции с повышением прав.
        var report = new ElevatedRunReport(12, 3, 1, 0, 4096, 2048, "20260905-000000-abcdef", null);

        var restored = JsonSerializer.Deserialize<ElevatedRunReport>(JsonSerializer.Serialize(report));

        Assert.NotNull(restored);
        Assert.Equal(12, restored.Succeeded);
        Assert.Equal(2048, restored.ActuallyFreedBytes);

        // Без сеанса кнопка отката не имеет смысла.
        Assert.Equal("20260905-000000-abcdef", restored.SessionId);
    }
}

/// <summary>Настройки программы.</summary>
public sealed class AppSettingsTests
{
    private static readonly DateTime Now = new(2026, 9, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Первая_проверка_обновлений_выполняется_сразу()
    {
        Assert.True(new AppSettings().ShouldCheckNow(Now));
    }

    [Fact]
    public void Повторная_проверка_в_тот_же_день_не_выполняется()
    {
        // Обращаться в сеть при каждом запуске незачем, и человека это раздражает.
        var settings = new AppSettings { LastUpdateCheckUtc = Now.AddHours(-3) };

        Assert.False(settings.ShouldCheckNow(Now));
    }

    [Fact]
    public void Через_сутки_проверка_снова_выполняется()
    {
        var settings = new AppSettings { LastUpdateCheckUtc = Now.AddHours(-25) };

        Assert.True(settings.ShouldCheckNow(Now));
    }

    [Fact]
    public void Отключённая_проверка_не_выполняется_никогда()
    {
        // Политика подписи обещает пользователю, что сетевое обращение отключается
        // полностью. Обещание должно исполняться, а не смягчаться.
        var settings = new AppSettings { CheckForUpdates = false, LastUpdateCheckUtc = null };

        Assert.False(settings.ShouldCheckNow(Now));
        Assert.False(settings.ShouldCheckNow(Now.AddYears(1)));
    }
}
