using Vacate.App;
using Xunit;

namespace Vacate.Tests.Ui;

/// <summary>Проверки отчёта об ошибке.</summary>
/// <remarks>
/// Отчёт предназначен для пересылки другому человеку, поэтому личные данные
/// из него должны исчезать. Оставить их означало бы, что программа, созданная
/// ради аккуратного обращения с чужими данными, сама их разглашает.
/// </remarks>
public sealed class CrashHandlerTests
{
    [Fact]
    public void Путь_к_профилю_пользователя_убирается_из_отчёта()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var text = $"Ошибка при обработке файла {profile}\\Documents\\секрет.txt";

        var sanitized = CrashHandlerAccessor.Sanitize(text);

        Assert.DoesNotContain(profile, sanitized, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<пользователь>", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void Имя_пользователя_убирается_из_отчёта()
    {
        var userName = Environment.UserName;
        var text = $"Не удалось прочитать данные учётной записи {userName}";

        var sanitized = CrashHandlerAccessor.Sanitize(text);

        Assert.DoesNotContain(userName, sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Текст_без_личных_данных_не_меняется()
    {
        const string text = "Не удалось открыть C:\\Windows\\Temp\\file.tmp";

        Assert.Equal(text, CrashHandlerAccessor.Sanitize(text));
    }

    [Fact]
    public void Пустой_текст_не_роняет_очистку()
    {
        Assert.Equal(string.Empty, CrashHandlerAccessor.Sanitize(string.Empty));
    }
}

/// <summary>Доступ к внутреннему методу очистки отчёта.</summary>
internal static class CrashHandlerAccessor
{
    public static string Sanitize(string text)
    {
        var method = typeof(App.App).Assembly
            .GetType("Vacate.App.CrashHandler")!
            .GetMethod("Sanitize", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        return (string)method.Invoke(null, [text])!;
    }
}
