using Vacate.Abstractions.Model;
using Vacate.Abstractions.Safety;
using Vacate.Core.Safety;
using Vacate.Platform.Windows.Registry;
using Xunit;

namespace Vacate.Tests.Execution;

/// <summary>
/// Превращение найденных остатков в план удаления.
/// </summary>
/// <remarks>
/// Здесь проверяется место, где найденное впервые становится удаляемым. Ошибка на этом
/// шаге не приводит к красивому исключению — она приводит к удалению чужого каталога,
/// поэтому проверок больше, чем кажется нужным для трёх десятков строк кода.
/// </remarks>
public sealed class LeftoverPlanBuilderTests : IDisposable
{
    private readonly string _sandbox;

    public LeftoverPlanBuilderTests()
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

    private static InstalledApp Fake(string name = "Zzqxwv Test") => new(
        Id: "{test}",
        DisplayName: name,
        Version: "1.0",
        Publisher: "Zzqxwv",
        InstallLocation: null,
        UninstallCommand: "uninstall.exe",
        QuietUninstallCommand: null,
        InstallDate: null,
        EstimatedSizeBytes: 0,
        Scope: InstallScope.Machine,
        Is32BitOnWin64: false,
        IconPath: null);

    private string CreateDirectory(string name)
    {
        var path = Path.Combine(_sandbox, name);
        Directory.CreateDirectory(path);
        return path;
    }

    [Theory]
    [InlineData(@"HKCU\SOFTWARE\Zzqxwv", RegistryHiveKind.CurrentUser, @"SOFTWARE\Zzqxwv")]
    [InlineData(@"HKLM\SOFTWARE\Zzqxwv\Settings", RegistryHiveKind.LocalMachine, @"SOFTWARE\Zzqxwv\Settings")]
    [InlineData(@"HKEY_CURRENT_USER\Software\App", RegistryHiveKind.CurrentUser, @"Software\App")]
    public void Путь_ветки_реестра_разбирается_обратно(string path, RegistryHiveKind hive, string subKey)
    {
        var target = LeftoverPlanBuilder.ParseRegistryPath(path);

        Assert.NotNull(target);
        Assert.Equal(hive, target.Hive);
        Assert.Equal(subKey, target.SubKeyPath);
        Assert.True(target.IsWholeKey);

        // Разрядность — обязательная часть адреса, а не деталь: её потеря даёт
        // классическую ошибку «удалили не ту ветку».
        Assert.Equal(RegistryViewKind.Registry64, target.View);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("HKCU")]
    [InlineData(@"HKCU\")]
    [InlineData(@"HKXX\SOFTWARE\App")]
    [InlineData(@"SOFTWARE\App")]
    public void Непонятный_путь_ветки_не_превращается_в_цель(string path)
    {
        // Догадка о том, что имел в виду источник, здесь стоила бы чужой ветки реестра.
        Assert.Null(LeftoverPlanBuilder.ParseRegistryPath(path));
    }

    [Fact]
    public void Каталог_попадает_в_план_как_перемещение_в_карантин()
    {
        var directory = CreateDirectory("leftover");

        var plan = new LeftoverPlanBuilder().Build(Fake(),
            [new LeftoverItem(directory, LeftoverKind.Directory, 1024, LeftoverConfidence.Certain, ["каталог установки"])]);

        var operation = Assert.IsType<DeleteFileOperation>(Assert.Single(plan.AllOperations));

        // Только карантин: остаток программы — единственное, что от неё осталось,
        // и ошибка выбора должна быть обратимой.
        Assert.Equal(DeleteDisposition.Quarantine, operation.Disposition);
        Assert.True(operation.Target.IsDirectory);
        Assert.Equal(directory, operation.Target.Path);
    }

    [Fact]
    public void Возможное_совпадение_объявляется_красным()
    {
        var directory = CreateDirectory("maybe");

        var plan = new LeftoverPlanBuilder().Build(Fake(),
            [new LeftoverItem(directory, LeftoverKind.Directory, 0, LeftoverConfidence.Possible, ["совпала часть имени"])]);

        // За таким объектом стоит одно совпадение части имени. Цена ошибки —
        // чужой каталог с данными, поэтому подтверждение должно быть осознанным.
        Assert.Equal(RiskLevel.Red, Assert.Single(plan.AllOperations).DeclaredRisk);
    }

    [Theory]
    [InlineData(LeftoverConfidence.Certain)]
    [InlineData(LeftoverConfidence.Likely)]
    public void Уверенная_находка_остаётся_жёлтой(LeftoverConfidence confidence)
    {
        var directory = CreateDirectory("sure");

        var plan = new LeftoverPlanBuilder().Build(Fake(),
            [new LeftoverItem(directory, LeftoverKind.Directory, 0, confidence, ["имя совпадает"])]);

        // Зелёным не бывает никогда: это удаление данных, а не временных файлов.
        Assert.Equal(RiskLevel.Yellow, Assert.Single(plan.AllOperations).DeclaredRisk);
    }

    [Fact]
    public void Исчезнувший_каталог_молча_выпадает_из_плана()
    {
        // Между показом списка и нажатием кнопки проходит время, за которое
        // человек мог удалить каталог сам. Это не ошибка и не повод падать.
        var plan = new LeftoverPlanBuilder().Build(Fake(),
            [new LeftoverItem(Path.Combine(_sandbox, "нет-такого"), LeftoverKind.Directory, 0, LeftoverConfidence.Certain, ["…"])]);

        Assert.Equal(0, plan.TotalCount);
    }

    [Fact]
    public void Пустой_выбор_даёт_пустой_план_без_групп()
    {
        var plan = new LeftoverPlanBuilder().Build(Fake(), []);

        Assert.Empty(plan.Groups);
        Assert.Equal(0, plan.TotalCount);
    }

    [Fact]
    public void Последствие_для_ветки_реестра_говорит_об_отсутствии_отката()
    {
        var plan = new LeftoverPlanBuilder().Build(Fake(),
            [new LeftoverItem(@"HKCU\SOFTWARE\Zzqxwv", LeftoverKind.RegistryKey, 0, LeftoverConfidence.Likely, ["имя совпадает"])]);

        var operation = Assert.Single(plan.AllOperations);
        var text = operation.Consequence.Translations?["ru"];

        // Карантин реестр не покрывает, и сказать об этом надо до нажатия,
        // а не обнаружиться при попытке отката.
        Assert.NotNull(text);
        Assert.Contains("карантин", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Размер_группы_складывается_из_целей()
    {
        var first = CreateDirectory("a");
        var second = CreateDirectory("b");

        var plan = new LeftoverPlanBuilder().Build(Fake(),
        [
            new LeftoverItem(first, LeftoverKind.Directory, 1000, LeftoverConfidence.Certain, ["…"]),
            new LeftoverItem(second, LeftoverKind.Directory, 24, LeftoverConfidence.Certain, ["…"]),
        ]);

        Assert.Equal(1024, plan.TotalSizeOnDiskBytes);
    }
}

/// <summary>
/// Защита критичных ветвей реестра.
/// </summary>
/// <remarks>
/// Проверки появились вместе с удалением остатков: до него операции над реестром
/// строил единственный модуль со своим списком исключений, и охраны на уровне
/// шлюза не было вовсе. Шлюз, полагающийся на добросовестность вызывающего,
/// защищает ровно до появления второго вызывающего.
/// </remarks>
public sealed class ProtectedRegistryGuardTests
{
    private static readonly GuardEnvironment Environment = new(
        TargetUserSid: "S-1-5-21-test",
        TargetUserProfilePath: @"C:\Users\test",
        FreeSpaceByVolume: new Dictionary<string, long> { [@"C:\"] = 100L * 1024 * 1024 * 1024 },
        IsEmergencyMode: false,
        AdvancedMode: false);

    private static OperationGroup GroupFor(string subKeyPath, RegistryHiveKind hive = RegistryHiveKind.LocalMachine) => new()
    {
        GroupId = "test",
        Title = LocalizedText.FromResource("test"),
        Operations =
        [
            new DeleteRegistryOperation
            {
                Id = "1",
                GroupId = "test",
                DeclaredRisk = RiskLevel.Yellow,
                Consequence = LocalizedText.FromResource("test"),
                Target = new RegistryTarget(hive, subKeyPath, ValueName: null, RegistryViewKind.Registry64),
            },
        ],
    };

    [Theory]
    [InlineData(@"SOFTWARE\Microsoft")]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion")]
    [InlineData(@"SOFTWARE\Classes")]
    [InlineData(@"SOFTWARE\Policies\Something")]
    [InlineData(@"SOFTWARE\WOW6432Node\Microsoft\Office")]
    [InlineData("SYSTEM")]
    [InlineData(@"SYSTEM\CurrentControlSet\Services")]
    [InlineData("SAM")]
    [InlineData("SECURITY")]
    public void Критичные_ветви_не_пропускаются(string subKeyPath)
    {
        var verdict = new ProtectedRegistryGuard().Evaluate(GroupFor(subKeyPath), Environment);

        Assert.Equal(GuardDecision.Deny, verdict.Decision);
    }

    [Theory]
    [InlineData("SOFTWARE")]
    [InlineData(@"SOFTWARE\WOW6432Node")]
    [InlineData("")]
    [InlineData(@"\")]
    public void Ветвь_целиком_удалить_нельзя(string subKeyPath)
    {
        // Удаление SOFTWARE целиком — не «крупная очистка», а неработающая система.
        var verdict = new ProtectedRegistryGuard().Evaluate(GroupFor(subKeyPath), Environment);

        Assert.Equal(GuardDecision.Deny, verdict.Decision);
    }

    [Theory]
    [InlineData(@"SOFTWARE\Zzqxwv")]
    [InlineData(@"SOFTWARE\WOW6432Node\Zzqxwv")]
    [InlineData(@"Software\Zzqxwv\Settings")]
    public void Ветвь_обычной_программы_пропускается(string subKeyPath)
    {
        var verdict = new ProtectedRegistryGuard().Evaluate(GroupFor(subKeyPath), Environment);

        Assert.Equal(GuardDecision.Allow, verdict.Decision);
    }

    [Theory]
    [InlineData(@"SOFTWARE\MicrosoftEdgeBackup")]
    [InlineData(@"SOFTWARE\ClassesHelper")]
    [InlineData("SYSTEMTOOLS")]
    public void Похожее_имя_не_считается_защищённой_ветвью(string subKeyPath)
    {
        // Сравнение посегментное, а не по началу строки: иначе законная ветка
        // с похожим именем оказалась бы неудаляемой без всякой причины.
        var verdict = new ProtectedRegistryGuard().Evaluate(GroupFor(subKeyPath), Environment);

        Assert.Equal(GuardDecision.Allow, verdict.Decision);
    }

    [Fact]
    public void Операции_над_файлами_эта_охрана_не_трогает()
    {
        var group = new OperationGroup
        {
            GroupId = "test",
            Title = LocalizedText.FromResource("test"),
            Operations =
            [
                new DeleteFileOperation
                {
                    Id = "1",
                    GroupId = "test",
                    DeclaredRisk = RiskLevel.Green,
                    Consequence = LocalizedText.FromResource("test"),
                    Target = new FileTarget(@"C:\Temp\file.tmp", IsDirectory: false, 100, FileTraits.None),
                    Disposition = DeleteDisposition.Quarantine,
                },
            ],
        };

        Assert.Equal(GuardDecision.Allow, new ProtectedRegistryGuard().Evaluate(group, Environment).Decision);
    }

    [Fact]
    public void Полный_набор_охраны_включает_защиту_реестра()
    {
        // Тест на повторение уже сделанной ошибки: список охраны раньше повторялся
        // в каждом месте сборки исполнителя, и новая проверка защищала лишь те вызовы,
        // где её не забыли дописать.
        var policy = PathPolicy.CreateDefault(@"C:\Windows", @"C:\", []);

        Assert.Contains(GuardSet.Group(policy), g => g is ProtectedRegistryGuard);
        Assert.Contains(GuardSet.Group(policy), g => g is ProtectedPathGuard);
    }
}
