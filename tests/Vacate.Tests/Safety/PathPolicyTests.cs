using Vacate.Core.Safety;
using Xunit;

namespace Vacate.Tests.Safety;

/// <summary>
/// Проверки политики путей.
/// </summary>
/// <remarks>
/// Первый тест здесь — защита от конкретной регрессии. В первой версии описания проекта
/// политика строилась на подсчёте сегментов пути, и такая проверка запрещала очистку
/// временных каталогов, то есть главную функцию продукта. Ошибку нашли на разборе,
/// а не в работающей программе; тест существует, чтобы она не вернулась.
/// </remarks>
public sealed class PathPolicyTests
{
    private const string Windows = @"C:\Windows";
    private const string SystemDrive = @"C:\";

    private static PathPolicy CreatePolicy(params string[] ownDirectories)
        => PathPolicy.CreateDefault(Windows, SystemDrive, ownDirectories);

    [Theory]
    [InlineData(@"C:\Windows\Temp")]
    [InlineData(@"C:\Windows\Temp\somefile.tmp")]
    [InlineData(@"C:\Windows\Prefetch")]
    [InlineData(@"C:\Windows\Logs\CBS")]
    public void Временные_каталоги_разрешены_несмотря_на_то_что_лежат_внутри_Windows(string path)
    {
        var decision = CreatePolicy().Evaluate(path);

        Assert.True(decision.IsAllowed, $"Очистка {path} — основная функция продукта, она не может быть запрещена");
    }

    [Theory]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"C:\Windows\SysWOW64\kernel32.dll")]
    [InlineData(@"C:\Windows\WinSxS\something")]
    [InlineData(@"C:\Boot\BCD")]
    [InlineData(@"C:\System Volume Information\file")]
    public void Системные_пути_запрещены_безусловно(string path)
    {
        var decision = CreatePolicy().Evaluate(path);

        Assert.False(decision.IsAllowed);
        Assert.NotNull(decision.Reason);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:\")]
    public void Корень_диска_целиком_недопустимая_цель(string path)
    {
        Assert.False(CreatePolicy().Evaluate(path).IsAllowed);
    }

    [Theory]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Users")]
    public void Защищённые_каталоги_целиком_недопустимая_цель(string path)
    {
        Assert.False(CreatePolicy().Evaluate(path).IsAllowed);
    }

    [Fact]
    public void Путь_внутри_защищённой_зоны_разрешён_но_требует_осторожности()
    {
        var decision = CreatePolicy().Evaluate(@"C:\Program Files\SomeApp\cache");

        Assert.True(decision.IsAllowed);
        Assert.True(decision.RequiresCaution, "Работа внутри Program Files обязана поднимать уровень риска");
    }

    [Fact]
    public void Сравнение_идёт_посегментно_а_не_по_префиксу_строки()
    {
        // «C:\Program Files Custom» не находится внутри «C:\Program Files»,
        // хотя как строка начинается с него. Наивное сравнение префиксов
        // накрыло бы посторонний каталог защитой и запретило бы его очистку.
        var decision = CreatePolicy().Evaluate(@"C:\Program Files Custom\app\temp");

        Assert.True(decision.IsAllowed);
        Assert.False(decision.RequiresCaution);
    }

    [Fact]
    public void Собственные_каталоги_программы_защищены()
    {
        // Каталог распаковки вспомогательных библиотек лежит во временной папке —
        // то есть ровно там, где программа чистит. Без этой защиты очистка сломала бы
        // саму программу посреди работы.
        const string ownTemp = @"C:\Users\Test\AppData\Local\Temp\.net\Vacate";
        var policy = CreatePolicy(ownTemp);

        Assert.False(policy.Evaluate(ownTemp).IsAllowed);
        Assert.False(policy.Evaluate(Path.Combine(ownTemp, "wpfgfx_cor3.dll")).IsAllowed);
    }

    [Fact]
    public void Регистр_символов_не_позволяет_обойти_защиту()
    {
        var policy = CreatePolicy();

        Assert.False(policy.Evaluate(@"c:\windows\system32\drivers").IsAllowed);
        Assert.False(policy.Evaluate(@"C:\WINDOWS\SYSTEM32").IsAllowed);
    }

    [Fact]
    public void Относительные_переходы_не_позволяют_выйти_из_разрешённой_зоны()
    {
        // Путь выглядит как временный каталог, но фактически указывает в System32.
        var policy = CreatePolicy();

        Assert.False(policy.Evaluate(@"C:\Windows\Temp\..\System32\drivers").IsAllowed);
    }

    [Fact]
    public void Добавленный_корень_очистки_разрешает_путь_внутри_защищённой_зоны()
    {
        var policy = CreatePolicy();
        policy.AddCleanRoot(@"C:\Users\Test\AppData\Local\Google\Chrome\User Data\Default\Cache");

        var decision = policy.Evaluate(@"C:\Users\Test\AppData\Local\Google\Chrome\User Data\Default\Cache\data_1");

        Assert.True(decision.IsAllowed);
        Assert.False(decision.RequiresCaution);
    }

    [Fact]
    public void Пустой_путь_отклоняется_а_не_роняет_программу()
    {
        Assert.False(CreatePolicy().Evaluate(string.Empty).IsAllowed);
        Assert.False(CreatePolicy().Evaluate("   ").IsAllowed);
    }
}
