using Vacate.Abstractions.Model;
using Vacate.Platform.Windows.Registry;
using Xunit;

namespace Vacate.Tests.Execution;

/// <summary>Проверки чтения автозапуска.</summary>
public sealed class StartupScannerTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --minimized", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Program Files\App\app.exe /background", @"C:\Program Files\App\app.exe")]
    [InlineData(@"\??\C:\Windows\system32\svc.exe", @"C:\Windows\system32\svc.exe")]
    [InlineData(@"C:\App\run.exe", @"C:\App\run.exe")]
    public void Путь_к_программе_выделяется_из_команды(string command, string expected)
    {
        Assert.Equal(expected, StartupScanner.ExtractImagePath(command));
    }

    [Fact]
    public void Пустая_команда_не_роняет_разбор()
    {
        Assert.Null(StartupScanner.ExtractImagePath(string.Empty));
        Assert.Null(StartupScanner.ExtractImagePath("   "));
    }

    [Theory]
    [InlineData("WinDefend")]
    [InlineData("wuauserv")]
    [InlineData("RpcSs")]
    [InlineData("Dhcp")]
    [InlineData("EventLog")]
    public void Критичные_службы_защищены_от_отключения(string serviceName)
    {
        // Обещание «испортить систему невозможно» здесь проверяется буквально:
        // защита Windows и основа работы системы не должны быть переключаемыми
        // ни в каком режиме.
        Assert.True(StartupScanner.IsProtectedService(serviceName));
    }

    [Theory]
    [InlineData("SomeVendorUpdater")]
    [InlineData("GoogleUpdate")]
    public void Обычные_службы_переключаемы(string serviceName)
    {
        Assert.False(StartupScanner.IsProtectedService(serviceName));
    }

    [Fact]
    public void Регистр_имени_службы_не_позволяет_обойти_защиту()
    {
        Assert.True(StartupScanner.IsProtectedService("windefend"));
        Assert.True(StartupScanner.IsProtectedService("WINDEFEND"));
    }

    [Fact]
    public void Сканирование_на_живой_системе_возвращает_записи()
    {
        var entries = new StartupScanner().Scan();

        // На любой работающей Windows автозапуск не бывает пустым.
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Name)));
    }

    [Fact]
    public void Защищённые_службы_помечены_как_только_для_просмотра_и_с_пояснением()
    {
        var entries = new StartupScanner().Scan();
        var locked = entries.Where(e => e.Control == StartupControl.ViewOnly).ToList();

        // Запрет без объяснения выглядит как поломка программы.
        Assert.All(locked, e => Assert.False(string.IsNullOrWhiteSpace(e.Note)));
    }

    [Theory]
    [InlineData(@"C:\Startup\app.lnk", true)]
    [InlineData(@"C:\Startup\tool.exe", true)]
    [InlineData(@"C:\Startup\script.bat", true)]
    [InlineData(@"C:\Startup\app.lnk.disabled", true)]
    [InlineData(@"C:\Startup\desktop.ini", false)]
    [InlineData(@"C:\Startup\readme.txt", false)]
    public void В_папке_автозагрузки_учитываются_только_запускаемые_файлы(string path, bool expected)
    {
        // Windows держит в этих папках служебный файл настроек отображения.
        // Проверка на живой машине показывала его как программу с именем «desktop».
        Assert.Equal(expected, StartupScanner.IsLaunchable(path));
    }

    [Fact]
    public void Служебные_файлы_не_попадают_в_список_на_живой_системе()
    {
        var entries = new StartupScanner().Scan();

        Assert.DoesNotContain(entries, e =>
            string.Equals(e.Name, "desktop", StringComparison.OrdinalIgnoreCase)
            && e.Source == StartupSource.StartupFolder);
    }

    [Fact]
    public void Драйверы_не_попадают_в_список_автозапуска()
    {
        // Показывать драйверы вперемешку с обновлятелями — верный способ
        // подтолкнуть пользователя отключить то, без чего не работает железо.
        var entries = new StartupScanner().Scan();

        Assert.DoesNotContain(entries, e =>
            e.ImagePath?.EndsWith(".sys", StringComparison.OrdinalIgnoreCase) == true);
    }
}
