using Vacate.Abstractions.Model;
using Vacate.Platform.Windows.Registry;
using Xunit;

namespace Vacate.Tests.Execution;

/// <summary>Проверки модели установленной программы и сканера списка.</summary>
public sealed class InstalledAppTests
{
    private static InstalledApp Create(string name, string? uninstall = "setup.exe /uninstall") => new(
        Id: "{test}",
        DisplayName: name,
        Version: "1.0",
        Publisher: "Test",
        InstallLocation: null,
        UninstallCommand: uninstall,
        QuietUninstallCommand: null,
        InstallDate: null,
        EstimatedSizeBytes: 0,
        Scope: InstallScope.Machine,
        Is32BitOnWin64: false,
        IconPath: null);

    [Theory]
    [InlineData("Microsoft Visual C++ 2015-2022 Redistributable (x64)")]
    [InlineData("Microsoft .NET Runtime - 8.0.11 (x64)")]
    [InlineData("Microsoft Edge WebView2 Runtime")]
    public void Среды_выполнения_распознаются(string name)
    {
        // Они занимают заметную часть списка, и удаление вслепую ломает
        // работающие программы, которые от них зависят.
        Assert.True(Create(name).LooksLikeRuntime);
    }

    [Theory]
    [InlineData("Google Chrome")]
    [InlineData("Telegram Desktop")]
    [InlineData("LibreOffice")]
    public void Обычные_программы_не_считаются_средами_выполнения(string name)
    {
        Assert.False(Create(name).LooksLikeRuntime);
    }

    [Fact]
    public void Программа_без_команды_удаления_помечается_как_неудаляемая()
    {
        // Такие записи в списке есть всегда. Показывать для них активную кнопку
        // «удалить» — значит обещать невыполнимое.
        Assert.False(Create("Что-то", uninstall: null).CanUninstall);
        Assert.False(Create("Что-то", uninstall: "  ").CanUninstall);
        Assert.True(Create("Что-то").CanUninstall);
    }

    [Fact]
    public void Сканер_возвращает_непустой_список_без_служебных_записей()
    {
        // Проверка на настоящем реестре машины: список не может быть пустым,
        // и в нём не должно быть записей без названия.
        var apps = new InstalledAppsScanner().Scan();

        Assert.NotEmpty(apps);
        Assert.All(apps, app => Assert.False(string.IsNullOrWhiteSpace(app.DisplayName)));
    }

    [Fact]
    public void Список_отсортирован_по_названию()
    {
        var apps = new InstalledAppsScanner().Scan();
        var sorted = apps.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();

        Assert.Equal(sorted.Select(a => a.DisplayName), apps.Select(a => a.DisplayName));
    }

    [Fact]
    public void Записи_не_дублируются()
    {
        // Одна и та же программа может быть записана и в 32-, и в 64-разрядном
        // представлении реестра. Показывать её дважды нельзя.
        var apps = new InstalledAppsScanner().Scan();
        var duplicates = apps
            .GroupBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }
}
