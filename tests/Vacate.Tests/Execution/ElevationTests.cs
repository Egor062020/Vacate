using Vacate.Abstractions.Model;
using Vacate.Platform.Windows.Files;
using Xunit;

namespace Vacate.Tests.Execution;

/// <summary>Проверки определения необходимости повышенных прав.</summary>
/// <remarks>
/// Ошибка в любую сторону обходится дорого: лишний запрос прав раздражает и приучает
/// нажимать «да» не глядя, а пропущенный оставляет операцию падать с непонятным отказом.
/// </remarks>
public sealed class ElevationBrokerTests
{
    private static MutationPlan PlanWith(params PlannedOperation[] operations) => new()
    {
        PlanId = "test",
        Origin = "tests",
        Groups =
        [
            new OperationGroup
            {
                GroupId = "g1",
                Title = LocalizedText.FromResource("Test"),
                Operations = operations,
                SizeOnDiskBytes = 0,
            },
        ],
    };

    private static DeleteFileOperation FileOp(string path) => new()
    {
        Id = "op",
        GroupId = "g1",
        DeclaredRisk = RiskLevel.Green,
        Consequence = LocalizedText.FromResource("Test"),
        Target = new FileTarget(path, false, 1024, FileTraits.None),
        Disposition = DeleteDisposition.Permanent,
    };

    private static DeleteRegistryOperation RegistryOp(RegistryHiveKind hive) => new()
    {
        Id = "op",
        GroupId = "g1",
        DeclaredRisk = RiskLevel.Yellow,
        Consequence = LocalizedText.FromResource("Test"),
        Target = new RegistryTarget(hive, @"SOFTWARE\Test", "Value", RegistryViewKind.Registry64),
    };

    [Fact]
    public void Очистка_своего_временного_каталога_прав_не_требует()
    {
        // Основной сценарий продукта. Если бы он требовал администратора,
        // программу не смог бы запустить никто без пароля админа.
        var plan = PlanWith(FileOp(Path.Combine(Path.GetTempPath(), "old.tmp")));

        Assert.False(ElevationBroker.RequiresElevation(plan));
    }

    [Fact]
    public void Работа_в_общесистемной_ветке_реестра_требует_прав()
    {
        Assert.True(ElevationBroker.RequiresElevation(PlanWith(RegistryOp(RegistryHiveKind.LocalMachine))));
    }

    [Fact]
    public void Работа_в_ветке_пользователя_прав_не_требует()
    {
        Assert.False(ElevationBroker.RequiresElevation(PlanWith(RegistryOp(RegistryHiveKind.CurrentUser))));
    }

    [Theory]
    [InlineData(@"C:\Windows\Temp\file.tmp")]
    [InlineData(@"C:\Program Files\App\cache.dat")]
    [InlineData(@"C:\ProgramData\App\log.txt")]
    public void Системные_области_требуют_прав(string path)
    {
        Assert.True(ElevationBroker.IsSystemArea(path));
    }

    [Fact]
    public void Личные_папки_пользователя_прав_не_требуют()
    {
        var userFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "file.zip");

        Assert.False(ElevationBroker.IsSystemArea(userFile));
    }

    [Fact]
    public void Пустой_путь_не_роняет_проверку()
    {
        Assert.False(ElevationBroker.IsSystemArea(string.Empty));
        Assert.False(ElevationBroker.IsSystemArea("   "));
    }

    [Fact]
    public void Пустой_план_прав_не_требует()
    {
        var plan = new MutationPlan { PlanId = "x", Origin = "tests", Groups = [] };

        Assert.False(ElevationBroker.RequiresElevation(plan));
    }

    [Fact]
    public async Task Отсутствующий_исполнитель_даёт_понятное_сообщение()
    {
        var result = await new ElevationBroker().ExecuteElevatedAsync(
            PlanWith(FileOp(@"C:\Windows\Temp\x.tmp")),
            @"C:\нет-такого-исполнителя.exe",
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("не найден", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
