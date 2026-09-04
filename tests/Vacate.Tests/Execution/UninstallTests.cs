using Vacate.Abstractions.Model;
using Vacate.Platform.Windows.Registry;
using Xunit;

namespace Vacate.Tests.Execution;

/// <summary>Разбор команд удаления, приходящих из реестра.</summary>
/// <remarks>
/// Формат этих строк ничем не регламентирован: их пишут сотни разных установщиков,
/// кто во что горазд. Наивное разделение по первому пробелу ломается на путях
/// вида «C:\Program Files\...», а таких большинство.
/// </remarks>
public sealed class UninstallCommandTests
{
    [Fact]
    public void Путь_в_кавычках_разбирается_целиком()
    {
        var (exe, args) = UninstallRunner.SplitCommand("\"C:\\Program Files\\App\\uninstall.exe\" /S");

        Assert.Equal(@"C:\Program Files\App\uninstall.exe", exe);
        Assert.Equal("/S", args);
    }

    [Fact]
    public void Путь_с_пробелами_без_кавычек_не_обрезается_по_первому_пробелу()
    {
        // Разделение по пробелу дало бы «C:\Program» — файл, которого не существует.
        var (exe, args) = UninstallRunner.SplitCommand(@"C:\Program Files\App\unins000.exe /SILENT");

        Assert.Equal(@"C:\Program Files\App\unins000.exe", exe);
        Assert.Equal("/SILENT", args);
    }

    [Fact]
    public void Команда_системного_установщика_разбирается()
    {
        var (exe, args) = UninstallRunner.SplitCommand("MsiExec.exe /X{12345678-1234-1234-1234-123456789012}");

        Assert.Equal("MsiExec.exe", exe);
        Assert.Equal("/X{12345678-1234-1234-1234-123456789012}", args);
    }

    [Fact]
    public void Команда_без_аргументов_разбирается()
    {
        var (exe, args) = UninstallRunner.SplitCommand(@"C:\App\uninstall.exe");

        Assert.Equal(@"C:\App\uninstall.exe", exe);
        Assert.Equal(string.Empty, args);
    }

    [Fact]
    public void Лишние_пробелы_не_мешают()
    {
        var (exe, args) = UninstallRunner.SplitCommand("   \"C:\\App\\u.exe\"   /quiet   ");

        Assert.Equal(@"C:\App\u.exe", exe);
        Assert.Equal("/quiet", args);
    }
}

/// <summary>Построение слов для поиска остатков.</summary>
public sealed class LeftoverTokenTests
{
    [Theory]
    [InlineData("FreeCAD 1.1.1 (Установлено для текущего пользователя)", "freecad")]
    [InlineData("Python 3.12.10 (64-bit)", "python")]
    [InlineData("Telegram Desktop", "telegram")]
    [InlineData("LibreOffice 26.2.3.2", "libreoffice")]
    public void Название_очищается_от_версий_и_уточнений(string displayName, string expected)
    {
        // На диске каталог называется просто именем программы. Без очистки
        // поиск по полному названию с версией не нашёл бы ничего.
        Assert.Equal(expected, LeftoverScanner.BuildSearchToken(displayName));
    }

    [Theory]
    [InlineData("jq")]
    [InlineData("uv")]
    [InlineData("Git")]
    public void Слишком_короткие_названия_для_поиска_не_используются(string displayName)
    {
        // «git» совпадёт с десятком посторонних каталогов, «uv» — с сотней.
        // Лучше не найти остатки, чем предложить удалить чужое.
        Assert.Null(LeftoverScanner.BuildSearchToken(displayName));
    }

    [Theory]
    [InlineData("Microsoft Corporation")]
    [InlineData("Google LLC")]
    [InlineData("Intel Corporation")]
    [InlineData("NVIDIA Corporation")]
    public void Крупные_издатели_для_поиска_не_используются(string publisher)
    {
        // У них десятки продуктов и общие каталоги: поиск по имени издателя
        // гарантированно утащит файлы других программ.
        Assert.Null(LeftoverScanner.BuildPublisherToken(publisher));
    }

    [Fact]
    public void Обычный_издатель_для_поиска_годится()
    {
        Assert.Equal("obsidian", LeftoverScanner.BuildPublisherToken("Obsidian.md"));
    }

    [Fact]
    public void Пустые_значения_не_роняют_разбор()
    {
        Assert.Null(LeftoverScanner.BuildSearchToken(string.Empty));
        Assert.Null(LeftoverScanner.BuildSearchToken("   "));
        Assert.Null(LeftoverScanner.BuildPublisherToken(null));
    }

    [Theory]
    [InlineData("Google Chrome", "Google LLC", "chrome")]
    [InlineData("Adobe Acrobat Reader", "Adobe Inc.", "acrobat")]
    [InlineData("NVIDIA GeForce Experience", "NVIDIA Corporation", "geforce")]
    public void Имя_производителя_в_начале_названия_пропускается(string name, string publisher, string expected)
    {
        // Проверка на живой машине показала, во что обходится обратное: для Chrome
        // сканер предлагал удалить каталог Google целиком — то есть данные Диска,
        // Earth и всех остальных продуктов заодно.
        Assert.Equal(expected, LeftoverScanner.BuildSearchToken(name, publisher));
    }

    [Fact]
    public void Если_после_имени_производителя_остаётся_короткое_слово_поиск_не_ведётся()
    {
        // «Microsoft Edge» — после отбрасывания производителя остаётся «edge»,
        // а это слово совпадает со слишком многим, чтобы искать по нему.
        Assert.Null(LeftoverScanner.BuildSearchToken("Microsoft Edge", "Microsoft Corporation"));
    }

    [Theory]
    [InlineData("Obsidian", "Obsidian.md", "obsidian")]
    [InlineData("Telegram Desktop", "Telegram FZ-LLC", "telegram")]
    public void Название_совпадающее_с_издателем_всё_равно_годится_для_поиска(string name, string publisher, string expected)
    {
        // У однопродуктовых компаний имя издателя и название программы совпадают.
        // Отбрасывать такое слово нельзя — оно единственное пригодное. Ошибка в первой
        // версии этой проверки приводила к поиску Telegram по слову «desktop»,
        // которое совпадает со слишком многим.
        Assert.Equal(expected, LeftoverScanner.BuildSearchToken(name, publisher));
    }
}

/// <summary>Поиск остатков на настоящей системе.</summary>
public sealed class LeftoverScannerTests
{
    private static InstalledApp Fake(string name, string? publisher = null, string? location = null) => new(
        Id: "{test}",
        DisplayName: name,
        Version: "1.0",
        Publisher: publisher,
        InstallLocation: location,
        UninstallCommand: "uninstall.exe",
        QuietUninstallCommand: null,
        InstallDate: null,
        EstimatedSizeBytes: 0,
        Scope: InstallScope.Machine,
        Is32BitOnWin64: false,
        IconPath: null);

    [Fact]
    public void Системные_каталоги_никогда_не_считаются_остатками()
    {
        // Программа с названием «Windows Something» не должна привести
        // к предложению удалить каталог Windows.
        var results = new LeftoverScanner().Scan(Fake("Windows Something"));

        Assert.DoesNotContain(results, r =>
            r.Path.EndsWith(@"\Windows", StringComparison.OrdinalIgnoreCase)
            || r.Path.Contains("Common Files", StringComparison.OrdinalIgnoreCase)
            || r.Path.Contains("System32", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Несуществующая_программа_не_даёт_ложных_находок()
    {
        var results = new LeftoverScanner().Scan(Fake("Zzqxwv Nonexistent Application"));

        Assert.Empty(results);
    }

    [Fact]
    public void Каталог_установки_отмечается_наивысшей_уверенностью()
    {
        var temp = Path.Combine(Path.GetTempPath(), "Vacate.Leftover." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var results = new LeftoverScanner().Scan(Fake("Zzqxwv Test", location: temp));

            var item = Assert.Single(results);
            Assert.Equal(LeftoverConfidence.Certain, item.Confidence);
            Assert.NotEmpty(item.Evidence);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void У_каждой_находки_есть_основание_для_показа_пользователю()
    {
        // Пользователь должен видеть, почему объект признан остатком.
        // Список без объяснений — это просьба довериться вслепую.
        var results = new LeftoverScanner().Scan(Fake("Obsidian", "Obsidian.md"));

        Assert.All(results, r => Assert.NotEmpty(r.Evidence));
    }
}
