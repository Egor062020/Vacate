using Vacate.Abstractions.Model;
using Vacate.Platform.Windows.Registry;
using Xunit;

namespace Vacate.Tests.Execution;

/// <summary>
/// Принудительное удаление: когда штатного деинсталлятора уже нет.
/// </summary>
/// <remarks>
/// Главное здесь — не «удалить любой ценой», а не предлагать свой способ там,
/// где работает штатный. Наш способ заведомо хуже: деинсталлятор знает про свою
/// программу службы, драйверы и задачи, о которых мы не догадаемся.
/// </remarks>
public sealed class ForcedUninstallTests
{
    private static InstalledApp App(string? uninstallCommand) => new(
        Id: "{test}",
        DisplayName: "Zzqxwv Test",
        Version: "1.0",
        Publisher: "Zzqxwv",
        InstallLocation: null,
        UninstallCommand: uninstallCommand,
        QuietUninstallCommand: null,
        InstallDate: null,
        EstimatedSizeBytes: 0,
        Scope: InstallScope.User,
        Is32BitOnWin64: false,
        IconPath: null);

    [Fact]
    public void Программа_без_команды_удаления_удаляется_принудительно()
    {
        Assert.True(ForcedUninstall.IsApplicable(App(null)));
        Assert.True(ForcedUninstall.IsApplicable(App("   ")));
    }

    [Fact]
    public void Запись_с_несуществующим_деинсталлятором_удаляется_принудительно()
    {
        // Программу снесли вручную, а запись осталась. Случай распространённый:
        // без принудительного удаления она висит в списке навсегда.
        Assert.True(ForcedUninstall.IsApplicable(App(@"""C:\Program Files\Zzqxwv\unins000.exe"" /S")));
    }

    [Fact]
    public void Пока_деинсталлятор_на_месте_принудительное_удаление_не_предлагается()
    {
        // Наш способ хуже штатного: мы удалим файлы, а программа могла держать
        // службу или задачу, про которые знает только её деинсталлятор.
        var existing = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "notepad.exe");

        Assert.True(File.Exists(existing), "для проверки нужен существующий файл");
        Assert.False(ForcedUninstall.IsApplicable(App($"\"{existing}\" /S")));
    }

    [Fact]
    public void Команда_системного_установщика_принудительной_не_считается()
    {
        // Путь не абсолютный: файл ищется в системных каталогах,
        // и судить о его существовании по строке нельзя.
        Assert.False(ForcedUninstall.IsApplicable(App("MsiExec.exe /X{12345678-1234-1234-1234-123456789012}")));
    }
}

/// <summary>
/// Слежение за установкой.
/// </summary>
/// <remarks>
/// Даёт основание, не зависящее от названий: «этого здесь не было пятнадцать минут
/// назад». Обычный поиск следов работает по совпадению имён и потому осторожничает.
/// </remarks>
public sealed class InstallWatcherTests
{
    private static SystemSnapshot Snapshot(
        IReadOnlyList<string> directories,
        IReadOnlyList<string>? keys = null,
        IReadOnlyList<string>? apps = null)
        => new(DateTime.UtcNow, directories, keys ?? [], apps ?? []);

    [Fact]
    public void Одинаковые_снимки_не_дают_различий()
    {
        var snapshot = Snapshot([@"C:\Program Files\App"], [@"HKCU\SOFTWARE\App"], ["{app}"]);

        Assert.True(new InstallWatcher().Compare(snapshot, snapshot).IsEmpty);
    }

    [Fact]
    public void Появившаяся_ветвь_реестра_попадает_в_различия()
    {
        var before = Snapshot([], [@"HKCU\SOFTWARE\Old"]);
        var after = Snapshot([], [@"HKCU\SOFTWARE\Old", @"HKCU\SOFTWARE\New"]);

        var difference = new InstallWatcher().Compare(before, after);

        Assert.Equal([@"HKCU\SOFTWARE\New"], difference.NewRegistryKeys);
        Assert.False(difference.IsEmpty);
    }

    [Fact]
    public void Появившаяся_запись_в_списке_установленного_попадает_в_различия()
    {
        var before = Snapshot([], null, ["{old}"]);
        var after = Snapshot([], null, ["{old}", "{new}"]);

        Assert.Equal(["{new}"], new InstallWatcher().Compare(before, after).NewApps);
    }

    [Fact]
    public void Исчезнувшее_различием_не_считается()
    {
        // Наблюдение отвечает на вопрос «что появилось», а не «что изменилось»:
        // предлагать удалить то, что и так исчезло, бессмысленно.
        var before = Snapshot([], [@"HKCU\SOFTWARE\Old", @"HKCU\SOFTWARE\Gone"]);
        var after = Snapshot([], [@"HKCU\SOFTWARE\Old"]);

        Assert.True(new InstallWatcher().Compare(before, after).IsEmpty);
    }

    [Fact]
    public void Несуществующий_каталог_в_различия_не_попадает()
    {
        // За время между снимками каталог мог появиться и исчезнуть:
        // предлагать удалить то, чего нет, — показывать выдумку.
        var before = Snapshot([]);
        var after = Snapshot([Path.Combine(Path.GetTempPath(), "Vacate.Tests", "нет-такого-каталога")]);

        Assert.Empty(new InstallWatcher().Compare(before, after).NewDirectories);
    }

    [Fact]
    public void Снимок_живой_системы_не_пуст()
    {
        // Снимок, не увидевший ни одного каталога, сравнивать бессмысленно —
        // он показал бы всю систему как «появившуюся».
        var snapshot = new InstallWatcher().Capture();

        Assert.NotEmpty(snapshot.Directories);
        Assert.NotEmpty(snapshot.RegistryKeys);
    }
}
