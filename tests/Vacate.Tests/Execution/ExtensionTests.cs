using Vacate.Abstractions.Model;
using Vacate.Platform.Windows.Files;
using Xunit;

namespace Vacate.Tests.Execution;

/// <summary>Перевод разрешений расширений.</summary>
public sealed class ExtensionPermissionTests
{
    [Theory]
    [InlineData("<all_urls>")]
    [InlineData("*://*/*")]
    [InlineData("http://*/*")]
    [InlineData("https://*/*")]
    public void Доступ_ко_всем_сайтам_распознаётся_как_наивысший_уровень(string raw)
    {
        // Это единственное, что человеку по-настоящему важно знать о расширении:
        // оно видит всё, что открывается, включая банк и почту.
        Assert.Equal(PermissionLevel.AllSites, ExtensionPermissions.Translate(raw).Level);
    }

    [Theory]
    [InlineData("https://docs.google.com/*")]
    [InlineData("https://example.com/*")]
    public void Доступ_к_конкретным_сайтам_не_приравнивается_ко_всем(string raw)
    {
        Assert.Equal(PermissionLevel.SomeSites, ExtensionPermissions.Translate(raw).Level);
    }

    [Theory]
    [InlineData("webRequest")]
    [InlineData("debugger")]
    [InlineData("proxy")]
    public void Опасные_возможности_помечены_наивысшим_уровнем(string raw)
    {
        Assert.Equal(PermissionLevel.AllSites, ExtensionPermissions.Translate(raw).Level);
    }

    [Theory]
    [InlineData("storage")]
    [InlineData("notifications")]
    [InlineData("contextMenus")]
    public void Безобидные_возможности_не_пугают_пользователя(string raw)
    {
        Assert.Equal(PermissionLevel.Harmless, ExtensionPermissions.Translate(raw).Level);
    }

    [Fact]
    public void Незнакомое_разрешение_не_выдаётся_за_безобидное()
    {
        // Мы просто не знаем, что это. Показывать «безопасно» в таком случае —
        // ровно то враньё, за которое продукт критикует конкурентов.
        var permission = ExtensionPermissions.Translate("someUnknownFuturePermission");

        Assert.Equal(PermissionLevel.Notable, permission.Level);
        Assert.Contains("неизвестно", permission.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Описание_даётся_человеческим_языком_а_не_техническим_термином()
    {
        var permission = ExtensionPermissions.Translate("cookies");

        Assert.DoesNotContain("cookies", permission.Description[..10], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("вход", permission.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Пустое_разрешение_не_роняет_разбор()
    {
        var permission = ExtensionPermissions.Translate(string.Empty);

        Assert.NotNull(permission.Description);
    }
}

/// <summary>Чтение расширений с живой системы.</summary>
public sealed class BrowserExtensionScannerTests
{
    [Fact]
    public void Сканирование_не_падает_и_возвращает_корректные_записи()
    {
        var extensions = new BrowserExtensionScanner().Scan();

        Assert.All(extensions, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Id));
            Assert.False(string.IsNullOrWhiteSpace(e.Name));
            Assert.False(string.IsNullOrWhiteSpace(e.Browser));
        });
    }

    [Fact]
    public void Названия_расшифровываются_а_не_остаются_ссылкой_на_перевод()
    {
        // Названия часто хранятся как «__MSG_appName__». Показать это пользователю
        // значит показать мусор вместо имени программы.
        var extensions = new BrowserExtensionScanner().Scan();

        Assert.DoesNotContain(extensions, e => e.Name.StartsWith("__MSG_", StringComparison.Ordinal));
    }

    [Fact]
    public void Уровень_прав_вычисляется_по_самому_серьёзному_разрешению()
    {
        var extension = new BrowserExtension(
            Id: "test",
            Name: "Тест",
            Version: "1.0",
            Browser: "Chrome",
            ProfileName: "Default",
            Permissions:
            [
                ExtensionPermissions.Translate("storage"),
                ExtensionPermissions.Translate("<all_urls>"),
            ],
            SizeBytes: 0,
            Path: string.Empty,
            LastUpdatedUtc: null);

        Assert.Equal(PermissionLevel.AllSites, extension.HighestLevel);
        Assert.True(extension.ReadsAllSites);
    }

    [Fact]
    public void Расширение_без_разрешений_считается_безобидным()
    {
        var extension = new BrowserExtension(
            "test", "Тест", "1.0", "Chrome", "Default", [], 0, string.Empty, null);

        Assert.Equal(PermissionLevel.Harmless, extension.HighestLevel);
        Assert.False(extension.ReadsAllSites);
    }
}
