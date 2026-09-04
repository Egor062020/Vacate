using PurgeX.Abstractions.Model;
using PurgeX.Platform.Windows.Files;
using Xunit;

namespace PurgeX.Tests.Safety;

/// <summary>
/// Проверки карантина — основного механизма отката.
/// </summary>
/// <remarks>
/// Тесты работают на настоящих файлах. Заглушки здесь бесполезны: они доказали бы
/// только то, что заглушка возвращает то, что в неё заложили, а вопрос стоит иначе —
/// действительно ли файл возвращается на место таким же, каким был.
/// </remarks>
public sealed class QuarantineTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _storeRoot;

    public QuarantineTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "PurgeX.Quarantine.Tests", Guid.NewGuid().ToString("N"));
        _storeRoot = Path.Combine(_sandbox, "store");
        Directory.CreateDirectory(_sandbox);
        Directory.CreateDirectory(_storeRoot);
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
            // Уборка не должна ронять прогон тестов.
        }
    }

    private FileSystemQuarantine CreateQuarantine(TimeSpan? retention = null, long budget = 2L * 1024 * 1024 * 1024)
        => new(retention, budget, _storeRoot);

    private string CreateFile(string name, string content)
    {
        var path = Path.Combine(_sandbox, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static FileTarget Describe(string path)
        => new(path, IsDirectory: false, SizeOnDiskBytes: new FileInfo(path).Length, Traits: FileTraits.None);

    [Fact]
    public async Task Файл_помещается_в_карантин_и_исчезает_с_исходного_места()
    {
        var path = CreateFile("doc.txt", "важные данные");
        var quarantine = CreateQuarantine();

        var result = await quarantine.StoreAsync(Describe(path), CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.UndoToken);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Восстановление_возвращает_файл_с_прежним_содержимым()
    {
        const string content = "содержимое, которое нельзя потерять";
        var path = CreateFile("doc.txt", content);
        var quarantine = CreateQuarantine();

        var stored = await quarantine.StoreAsync(Describe(path), CancellationToken.None);
        var restored = await quarantine.RestoreAsync(stored.UndoToken!, CancellationToken.None);

        Assert.True(restored.Success);
        Assert.True(File.Exists(path));
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact]
    public async Task Восстановление_возвращает_атрибуты_а_не_только_содержимое()
    {
        // «Файл на месте» без прежних атрибутов — это не восстановление.
        var path = CreateFile("readonly.txt", "данные");
        File.SetAttributes(path, FileAttributes.ReadOnly | FileAttributes.Hidden);

        var quarantine = CreateQuarantine();
        var stored = await quarantine.StoreAsync(Describe(path), CancellationToken.None);
        var restored = await quarantine.RestoreAsync(stored.UndoToken!, CancellationToken.None);

        Assert.True(restored.Success);
        Assert.True(restored.AttributesRestored);

        var attributes = File.GetAttributes(path);
        Assert.True(attributes.HasFlag(FileAttributes.ReadOnly));
        Assert.True(attributes.HasFlag(FileAttributes.Hidden));

        File.SetAttributes(path, FileAttributes.Normal);
    }

    [Fact]
    public async Task Файл_только_для_чтения_всё_равно_помещается_в_карантин()
    {
        // Без снятия атрибута система откажет в перемещении, и объект ошибочно
        // попал бы в отчёт как занятый.
        var path = CreateFile("locked.txt", "данные");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var result = await CreateQuarantine().StoreAsync(Describe(path), CancellationToken.None);

        Assert.True(result.Success);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Восстановление_воссоздаёт_исчезнувший_родительский_каталог()
    {
        var directory = Path.Combine(_sandbox, "sub");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "doc.txt");
        File.WriteAllText(path, "данные");

        var quarantine = CreateQuarantine();
        var stored = await quarantine.StoreAsync(Describe(path), CancellationToken.None);

        // За время карантина исходный каталог исчез — обычное дело при удалении программы.
        Directory.Delete(directory, recursive: true);

        var restored = await quarantine.RestoreAsync(stored.UndoToken!, CancellationToken.None);

        Assert.True(restored.Success);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Превышение_бюджета_отклоняет_операцию_вместо_тихого_вытеснения()
    {
        // Тихое вытеснение старых записей обнулило бы возможность отката прошлых сессий,
        // причём незаметно для пользователя. Правильное поведение — честный отказ.
        var path = CreateFile("big.bin", new string('x', 5000));
        var quarantine = CreateQuarantine(budget: 1000);

        var result = await quarantine.StoreAsync(Describe(path), CancellationToken.None);

        Assert.False(result.Success);
        Assert.True(result.BudgetExceeded);
        Assert.True(File.Exists(path), "Файл обязан остаться на месте, раз откат обеспечить не удалось");
    }

    [Fact]
    public async Task Истёкшие_записи_убираются_а_свежие_остаются()
    {
        var expiring = CreateFile("old.txt", "старое");
        var fresh = CreateFile("new.txt", "свежее");

        var expired = CreateQuarantine(retention: TimeSpan.FromMilliseconds(-1));
        await expired.StoreAsync(Describe(expiring), CancellationToken.None);

        var kept = CreateQuarantine(retention: TimeSpan.FromDays(30));
        var keptResult = await kept.StoreAsync(Describe(fresh), CancellationToken.None);

        var removed = await kept.PurgeExpiredAsync(CancellationToken.None);

        Assert.Equal(1, removed);

        // Свежая запись по-прежнему восстановима.
        var restored = await kept.RestoreAsync(keptResult.UndoToken!, CancellationToken.None);
        Assert.True(restored.Success);
    }

    [Fact]
    public async Task Восстановление_по_неизвестному_ключу_сообщает_об_ошибке_а_не_молчит()
    {
        var result = await CreateQuarantine().RestoreAsync("нет-такого-ключа", CancellationToken.None);

        Assert.False(result.Success);
        Assert.NotNull(result.Reason);
    }

    [Fact]
    public async Task Пропажа_данных_из_карантина_честно_отражается_в_результате()
    {
        // Содержимое карантина может изъять антивирус. Рапортовать об успешном
        // восстановлении в этом случае нельзя.
        var path = CreateFile("doc.txt", "данные");
        var quarantine = CreateQuarantine();
        var stored = await quarantine.StoreAsync(Describe(path), CancellationToken.None);

        var storedFile = Directory
            .EnumerateFiles(Path.Combine(_storeRoot, FileSystemQuarantine.DirectoryName))
            .First(f => Path.GetFileName(f) == stored.UndoToken);
        File.Delete(storedFile);

        var restored = await quarantine.RestoreAsync(stored.UndoToken!, CancellationToken.None);

        Assert.False(restored.Success);
        Assert.NotNull(restored.Reason);
    }

    [Fact]
    public void Пути_карантина_распознаются_чтобы_исключить_их_из_сканирования()
    {
        // Иначе карта диска покажет карантин самой большой папкой, а поиск дубликатов
        // найдёт карантинные копии и предложит удалить оригинал.
        var quarantine = CreateQuarantine();

        Assert.True(quarantine.IsQuarantinePath(Path.Combine(_storeRoot, FileSystemQuarantine.DirectoryName, "abc")));
        Assert.False(quarantine.IsQuarantinePath(Path.Combine(_sandbox, "обычный.txt")));
    }
}
