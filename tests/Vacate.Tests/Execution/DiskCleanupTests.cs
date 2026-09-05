using Vacate.Abstractions.Model;
using Vacate.Platform.Windows.Files;
using Xunit;

namespace Vacate.Tests.Execution;

/// <summary>
/// Удаление файлов, найденных анализом диска.
/// </summary>
/// <remarks>
/// Единственное место в продукте, где под удаление попадают личные файлы человека.
/// Ошибка здесь не «неудобство», а потеря чужих данных, поэтому проверок больше,
/// чем требует объём кода.
/// </remarks>
public sealed class DiskCleanupPlanBuilderTests : IDisposable
{
    private readonly string _sandbox;

    public DiskCleanupPlanBuilderTests()
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

    private ScannedFile CreateFile(string name, FileTraits traits = FileTraits.None)
    {
        var path = Path.Combine(_sandbox, name);
        File.WriteAllText(path, "данные");

        return new ScannedFile(path, 1024, DateTime.UtcNow, VolumeSerial: 1, FileId: 1, traits);
    }

    [Fact]
    public void Личные_файлы_уходят_в_Корзину_а_не_в_карантин()
    {
        // Карантин — служебное хранилище, о котором человек знает только с наших слов.
        // Корзина — место, куда он привык заглядывать сам.
        var plan = new DiskCleanupPlanBuilder().ForFiles([CreateFile("big.bin")]);

        var operation = Assert.IsType<DeleteFileOperation>(Assert.Single(plan.AllOperations));

        Assert.Equal(DeleteDisposition.RecycleBin, operation.Disposition);
    }

    [Fact]
    public void Первая_копия_в_группе_не_удаляется_никогда()
    {
        var files = new[] { CreateFile("a.txt"), CreateFile("b.txt"), CreateFile("c.txt") };
        var group = new DuplicateGroup(files, 1024);

        var plan = new DiskCleanupPlanBuilder().ForDuplicates([group]);

        // Предложить удалить все экземпляры значит превратить освобождение места
        // в потерю данных.
        Assert.Equal(2, plan.TotalCount);

        var paths = plan.AllOperations.OfType<DeleteFileOperation>().Select(o => o.Target.Path).ToList();

        Assert.DoesNotContain(files[0].Path, paths);
        Assert.Contains(files[1].Path, paths);
        Assert.Contains(files[2].Path, paths);
    }

    [Fact]
    public void Файл_в_синхронизируемой_папке_объявляется_красным()
    {
        // Такой файл исчезнет на всех устройствах человека, а Корзина
        // на этом компьютере вернёт его только здесь.
        var plan = new DiskCleanupPlanBuilder().ForFiles([CreateFile("doc.txt", FileTraits.InCloudFolder)]);

        Assert.Equal(RiskLevel.Red, Assert.Single(plan.AllOperations).DeclaredRisk);
    }

    [Fact]
    public void Обычный_файл_объявляется_жёлтым_а_не_зелёным()
    {
        // Зелёным не бывает никогда: это чей-то файл, а не временные данные.
        var plan = new DiskCleanupPlanBuilder().ForFiles([CreateFile("photo.jpg")]);

        Assert.Equal(RiskLevel.Yellow, Assert.Single(plan.AllOperations).DeclaredRisk);
    }

    [Fact]
    public void Последствие_для_облачного_файла_говорит_про_все_устройства()
    {
        var plan = new DiskCleanupPlanBuilder().ForFiles([CreateFile("sync.txt", FileTraits.InCloudFolder)]);

        var text = Assert.Single(plan.AllOperations).Consequence.Translations?["ru"];

        Assert.NotNull(text);
        Assert.Contains("на всех ваших устройствах", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Исчезнувший_файл_молча_выпадает_из_плана()
    {
        var missing = new ScannedFile(
            Path.Combine(_sandbox, "нет-такого.bin"), 100, DateTime.UtcNow, 1, 1, FileTraits.None);

        Assert.Equal(0, new DiskCleanupPlanBuilder().ForFiles([missing]).TotalCount);
    }

    [Fact]
    public void Группа_из_одного_файла_не_даёт_операций()
    {
        // Копий нет — удалять нечего, и предлагать нечего.
        var group = new DuplicateGroup([CreateFile("lonely.txt")], 1024);

        Assert.Empty(new DiskCleanupPlanBuilder().ForDuplicates([group]).Groups);
    }

    [Fact]
    public void Размер_группы_складывается_из_удаляемых_файлов()
    {
        var files = new[] { CreateFile("x.bin"), CreateFile("y.bin") };

        Assert.Equal(2048, new DiskCleanupPlanBuilder().ForFiles(files).TotalSizeOnDiskBytes);
    }
}

/// <summary>
/// Распознавание синхронизируемых папок.
/// </summary>
/// <remarks>
/// Самый дорогой сценарий потери данных: удаление «лишней копии» в облачной папке
/// уносит файл со всех устройств человека, включая телефон, где он о существовании
/// этой программы не знает.
/// </remarks>
public sealed class CloudFoldersTests
{
    [Fact]
    public void Пустой_путь_не_роняет_проверку()
    {
        Assert.False(CloudFolders.Contains(string.Empty));
        Assert.False(CloudFolders.Contains("   "));
    }

    [Fact]
    public void Негодный_путь_не_роняет_проверку()
    {
        Assert.False(CloudFolders.Contains("<<не путь>>"));
    }

    [Fact]
    public void Обычная_системная_папка_облачной_не_считается()
    {
        Assert.False(CloudFolders.Contains(@"C:\Windows\System32"));
    }

    [Fact]
    public void Найденные_корни_существуют_на_диске()
    {
        // Список собирается из переменных среды, реестра и стандартных имён.
        // Путь, которого нет, в него попасть не должен — иначе проверка
        // сравнивала бы с выдумкой.
        Assert.All(CloudFolders.KnownRoots, root => Assert.True(Directory.Exists(root), root));
    }

    [Fact]
    public void Папка_с_похожим_именем_рядом_с_облачной_не_считается_облачной()
    {
        // Сравнение посегментное: иначе «OneDriveArchive» рядом с «OneDrive»
        // молча получил бы красный уровень и лишние предупреждения.
        var root = CloudFolders.KnownRoots.FirstOrDefault();

        if (root is null)
        {
            // На машине без облачных клиентов проверять нечего.
            return;
        }

        Assert.True(CloudFolders.Contains(Path.Combine(root, "файл.txt")));
        Assert.False(CloudFolders.Contains(root + "Archive"));
    }
}
