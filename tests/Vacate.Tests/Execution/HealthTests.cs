using Vacate.Abstractions.Model;
using Vacate.Platform.Windows.Files;
using Xunit;

namespace Vacate.Tests.Execution;

/// <summary>Проверки диагностики дисков.</summary>
public sealed class DiskHealthTests
{
    private static DiskHealth Create(
        DiskHealthStatus status = DiskHealthStatus.Healthy,
        int? wear = null,
        int? temperature = null,
        params string[] unavailable) => new(
        Model: "Test Disk",
        MediaType: "твердотельный",
        SizeBytes: 512L * 1024 * 1024 * 1024,
        Health: status,
        TemperatureCelsius: temperature,
        WearPercent: wear,
        PowerOnHours: 1000,
        ReadErrorsTotal: 0,
        Unavailable: unavailable);

    [Fact]
    public void Исправный_диск_не_требует_внимания()
    {
        Assert.False(Create().NeedsAttention);
    }

    [Theory]
    [InlineData(DiskHealthStatus.Warning)]
    [InlineData(DiskHealthStatus.Unhealthy)]
    public void Проблемное_состояние_требует_внимания(DiskHealthStatus status)
    {
        Assert.True(Create(status).NeedsAttention);
    }

    [Fact]
    public void Высокий_износ_требует_внимания_даже_при_общем_вердикте_исправен()
    {
        // Диск может считаться исправным и при этом дорабатывать ресурс.
        // Предупредить надо до отказа, а не после.
        Assert.True(Create(wear: 85).NeedsAttention);
    }

    [Fact]
    public void Перегрев_требует_внимания()
    {
        Assert.True(Create(temperature: 65).NeedsAttention);
    }

    [Fact]
    public void Отсутствие_данных_не_выдаётся_за_исправность()
    {
        // Главное требование к этому разделу: молчание диска — это «неизвестно»,
        // а не «всё хорошо». Внешние диски через переходники обычно молчат.
        var disk = Create(DiskHealthStatus.Unknown, unavailable: "счётчики надёжности");

        Assert.Equal(DiskHealthStatus.Unknown, disk.Health);
        Assert.NotEmpty(disk.Unavailable);
    }

    [Fact]
    public void Чтение_на_живой_системе_не_падает()
    {
        var disks = new DiskHealthReader().Read();

        // Список может быть пуст на урезанных сборках Windows — это допустимо.
        // Недопустимо падение или выдуманные данные.
        Assert.All(disks, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Model));
            Assert.False(string.IsNullOrWhiteSpace(d.MediaType));
        });
    }

    [Fact]
    public void Недоступные_показатели_перечислены_явно()
    {
        var disks = new DiskHealthReader().Read();

        // Если показателя нет, он обязан быть назван в списке недоступного,
        // а не просто отсутствовать в выводе.
        Assert.All(disks, d =>
        {
            if (d.TemperatureCelsius is null && d.Unavailable.Count == 0)
            {
                Assert.Fail("Отсутствующий показатель не объяснён пользователю");
            }
        });
    }
}

/// <summary>Проверки контроля целостности системных файлов.</summary>
public sealed class SystemIntegrityTests
{
    [Fact]
    public async Task Без_прав_администратора_возвращается_понятное_объяснение()
    {
        // Программа работает без повышения прав, поэтому обычный запуск проверки
        // невозможен — и сказать об этом надо до запуска, а не кодом ошибки после.
        var result = await new SystemIntegrityChecker().RunAsync(null, CancellationToken.None);

        if (!SystemIntegrityChecker.IsElevated())
        {
            Assert.Equal(IntegrityStatus.NeedsElevation, result.Status);
            Assert.Contains("администратор", result.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Определение_прав_не_падает()
    {
        // Результат зависит от того, как запущены тесты, поэтому проверяем
        // только отсутствие исключения.
        _ = SystemIntegrityChecker.IsElevated();
    }
}
