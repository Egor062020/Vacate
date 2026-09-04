using Vacate.Platform.Windows.Files;
using Xunit;

namespace Vacate.Tests.Execution;

/// <summary>Проверки анализа занятого места.</summary>
public sealed class DiskAnalyzerTests : IDisposable
{
    private readonly string _sandbox;

    public DiskAnalyzerTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "Vacate.Disk.Tests", Guid.NewGuid().ToString("N"));
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
            // Уборка не должна ронять прогон.
        }
    }

    /// <summary>Создать файл заданного размера. Дубликаты ищутся только среди крупных файлов.</summary>
    private string CreateFile(string name, string content, int repeat = 1)
    {
        var path = Path.Combine(_sandbox, name);
        var directory = Path.GetDirectoryName(path);

        if (directory is not null && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, string.Concat(Enumerable.Repeat(content, repeat)));
        return path;
    }

    [Theory]
    [InlineData("film.mp4", "Видео")]
    [InlineData("photo.JPG", "Изображения")]
    [InlineData("archive.zip", "Архивы и образы")]
    [InlineData("setup.exe", "Установщики и программы")]
    [InlineData("report.docx", "Документы")]
    [InlineData("library.dll", "Служебные файлы программ")]
    [InlineData("output.log", "Временные файлы и журналы")]
    [InlineData("noext", "Прочее")]
    public void Файлы_распределяются_по_понятным_видам(string fileName, string expected)
    {
        Assert.Equal(expected, DiskAnalyzer.CategoryOf(fileName));
    }

    [Fact]
    public void Одинаковые_файлы_находятся()
    {
        // Дубликаты ищутся только среди файлов от мегабайта: на мелочи выигрыш
        // не окупает чтения с диска.
        var content = new string('x', 1024);
        CreateFile("a.bin", content, 1100);
        CreateFile("sub/b.bin", content, 1100);

        var result = new DiskAnalyzer().Analyze(_sandbox);

        var group = Assert.Single(result.Duplicates);
        Assert.Equal(2, group.Files.Count);
        Assert.True(group.RecoverableBytes > 0);
    }

    [Fact]
    public void Разные_файлы_одинакового_размера_дубликатами_не_считаются()
    {
        // Совпадение размера — только первый признак. Без сверки содержимого
        // пользователю предложили бы удалить непохожие файлы.
        CreateFile("a.bin", new string('x', 1024), 1100);
        CreateFile("b.bin", new string('y', 1024), 1100);

        var result = new DiskAnalyzer().Analyze(_sandbox);

        Assert.Empty(result.Duplicates);
    }

    [Fact]
    public void Жёсткая_ссылка_на_файл_не_считается_его_дубликатом()
    {
        // Это защита от потери данных: жёсткая ссылка — не копия, а второе имя
        // того же файла. Предложить удалить «копию» значит предложить удалить
        // единственный экземпляр.
        var original = CreateFile("original.bin", new string('z', 1024), 1100);
        var link = Path.Combine(_sandbox, "link.bin");

        if (!CreateHardLink(link, original, IntPtr.Zero))
        {
            // Файловая система может не поддерживать жёсткие ссылки — тогда проверять нечего.
            return;
        }

        var result = new DiskAnalyzer().Analyze(_sandbox);

        Assert.Empty(result.Duplicates);
    }

    [Fact]
    public void Итоги_считаются_по_всем_найденным_файлам()
    {
        CreateFile("a.txt", "данные");
        CreateFile("sub/b.txt", "данные");
        CreateFile("sub/deep/c.txt", "данные");

        var result = new DiskAnalyzer().Analyze(_sandbox);

        Assert.Equal(3, result.TotalFilesScanned);
        Assert.True(result.TotalBytesScanned > 0);
    }

    [Fact]
    public void Крупные_файлы_отсортированы_по_убыванию()
    {
        CreateFile("small.bin", "x", 100);
        CreateFile("big.bin", "x", 10000);
        CreateFile("medium.bin", "x", 1000);

        var result = new DiskAnalyzer().Analyze(_sandbox);

        var sizes = result.LargestFiles.Select(f => f.SizeOnDiskBytes).ToList();
        Assert.Equal(sizes.OrderByDescending(s => s), sizes);
    }

    [Fact]
    public void Пустая_папка_не_роняет_анализ()
    {
        var result = new DiskAnalyzer().Analyze(_sandbox);

        Assert.Equal(0, result.TotalFilesScanned);
        Assert.Empty(result.Duplicates);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
