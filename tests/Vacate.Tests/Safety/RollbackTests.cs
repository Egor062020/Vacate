using Vacate.Abstractions.Model;
using Vacate.Platform.Windows.Files;
using Vacate.Platform.Windows.Registry;
using Xunit;

namespace Vacate.Tests.Safety;

/// <summary>
/// Средства возврата, которых не покрывает карантин.
/// </summary>
/// <remarks>
/// Файл можно передвинуть и вернуть, ветку реестра — нет. До появления этих средств
/// об этом честно предупреждали перед нажатием, но предупреждение не заменяет
/// возможности всё вернуть.
/// </remarks>
public sealed class RegistryBackupTests
{
    [Theory]
    [InlineData(RegistryHiveKind.LocalMachine, @"SOFTWARE\App", @"HKLM\SOFTWARE\App")]
    [InlineData(RegistryHiveKind.CurrentUser, @"SOFTWARE\App", @"HKCU\SOFTWARE\App")]
    [InlineData(RegistryHiveKind.Users, @"S-1-5-21\Software", @"HKU\S-1-5-21\Software")]
    public void Цель_приводится_к_виду_понятному_штатной_выгрузке(
        RegistryHiveKind hive, string subKey, string expected)
    {
        var target = new RegistryTarget(hive, subKey, ValueName: null, RegistryViewKind.Registry64);

        Assert.Equal(expected, RegistryBackup.Format(target));
    }

    [Fact]
    public void Лишние_разделители_в_пути_убираются()
    {
        var target = new RegistryTarget(RegistryHiveKind.CurrentUser, @"\SOFTWARE\App\", null, RegistryViewKind.Registry64);

        Assert.Equal(@"HKCU\SOFTWARE\App", RegistryBackup.Format(target));
    }

    [Fact]
    public void Пустой_путь_целью_выгрузки_не_становится()
    {
        // Иначе выгрузили бы раздел целиком — сотни мегабайт вместо одной ветки.
        var target = new RegistryTarget(RegistryHiveKind.CurrentUser, "   ", null, RegistryViewKind.Registry64);

        Assert.Null(RegistryBackup.Format(target));
    }

    [Fact]
    public async Task План_без_ветвей_реестра_копии_не_требует()
    {
        // Запускать выгрузку ради плана из одних файлов — впустую тратить время
        // человека на внешнюю программу.
        var plan = new MutationPlan
        {
            PlanId = "p",
            Origin = "test",
            Groups =
            [
                new OperationGroup
                {
                    GroupId = "g",
                    Title = LocalizedText.FromResource("t"),
                    Operations =
                    [
                        new DeleteFileOperation
                        {
                            Id = "1",
                            GroupId = "g",
                            DeclaredRisk = RiskLevel.Green,
                            Consequence = LocalizedText.FromResource("c"),
                            Target = new FileTarget(@"C:\Temp\a.tmp", false, 10, FileTraits.None),
                            Disposition = DeleteDisposition.Permanent,
                        },
                    ],
                },
            ],
        };

        Assert.Null(await new RegistryBackup().SaveAsync(plan));
    }
}

/// <summary>
/// Точка восстановления системы.
/// </summary>
/// <remarks>
/// Последний рубеж, когда не помогли ни карантин, ни копия ветвей. Создание требует
/// прав администратора, поэтому здесь проверяется решение «нужна ли она вообще»:
/// именно от него зависит, будет ли рубеж поставлен.
/// </remarks>
public sealed class RestorePointTests
{
    private static MutationPlan PlanWith(PlannedOperation operation) => new()
    {
        PlanId = "p",
        Origin = "test",
        Groups =
        [
            new OperationGroup
            {
                GroupId = "g",
                Title = LocalizedText.FromResource("t"),
                Operations = [operation],
            },
        ],
    };

    private static DeleteFileOperation FileOperation(string path, bool isDirectory) => new()
    {
        Id = "1",
        GroupId = "g",
        DeclaredRisk = RiskLevel.Yellow,
        Consequence = LocalizedText.FromResource("c"),
        Target = new FileTarget(path, isDirectory, 100, FileTraits.None),
        Disposition = DeleteDisposition.Quarantine,
    };

    [Fact]
    public void Удаление_общей_ветви_реестра_заслуживает_точки()
    {
        var operation = new DeleteRegistryOperation
        {
            Id = "1",
            GroupId = "g",
            DeclaredRisk = RiskLevel.Yellow,
            Consequence = LocalizedText.FromResource("c"),
            Target = new RegistryTarget(RegistryHiveKind.LocalMachine, @"SOFTWARE\App", null, RegistryViewKind.Registry64),
        };

        Assert.True(RestorePoint.IsWorthIt(PlanWith(operation)));
    }

    [Fact]
    public void Удаление_ветви_пользователя_точки_не_требует()
    {
        // Пользовательская ветка не меняет устройства системы, а точка стоит
        // времени и места на диске.
        var operation = new DeleteRegistryOperation
        {
            Id = "1",
            GroupId = "g",
            DeclaredRisk = RiskLevel.Yellow,
            Consequence = LocalizedText.FromResource("c"),
            Target = new RegistryTarget(RegistryHiveKind.CurrentUser, @"SOFTWARE\App", null, RegistryViewKind.Registry64),
        };

        Assert.False(RestorePoint.IsWorthIt(PlanWith(operation)));
    }

    [Fact]
    public void Удаление_каталога_программы_заслуживает_точки()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        Assert.True(RestorePoint.IsWorthIt(PlanWith(FileOperation(Path.Combine(programFiles, "App"), isDirectory: true))));
    }

    [Fact]
    public void Уборка_временных_файлов_точки_не_требует()
    {
        // Иначе точка создавалась бы при каждой еженедельной очистке —
        // и вытеснила бы собой все прежние.
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.False(RestorePoint.IsWorthIt(PlanWith(FileOperation(Path.Combine(windows, "Temp", "a.tmp"), isDirectory: false))));
    }

    [Fact]
    public void Файл_в_личной_папке_точки_не_требует()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.False(RestorePoint.IsWorthIt(PlanWith(FileOperation(Path.Combine(profile, "Downloads", "big.iso"), isDirectory: false))));
    }

    [Fact]
    public void Без_прав_администратора_отказ_объясняется_а_не_падает()
    {
        // Тесты идут без повышения прав, и это ровно тот случай,
        // который человек увидит чаще всего.
        var result = new RestorePoint().Create("Проверка");

        Assert.NotEqual(RestorePointStatus.Created, result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }
}
