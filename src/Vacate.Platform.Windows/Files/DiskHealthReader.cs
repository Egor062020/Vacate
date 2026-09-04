using System.Management;
using Vacate.Abstractions.Model;

namespace Vacate.Platform.Windows.Files;

/// <summary>
/// Читает состояние физических дисков.
/// </summary>
/// <remarks>
/// Универсального способа получить показатели диска не существует. Накопители с разными
/// интерфейсами отвечают по-разному, а внешние диски через переходники часто не отвечают
/// вовсе — производители переходников реализуют передачу этих команд каждый по-своему,
/// и поддержка их всех заняла бы годы (у специализированных программ на это и ушли годы).
///
/// Поэтому берётся путь, который Windows предоставляет сама: он покрывает большинство
/// внутренних дисков и не требует разбора двоичных структур от конкретного производителя.
/// Всё, чего узнать не удалось, попадает в список недоступного и показывается пользователю
/// прямо. Рисовать «состояние отличное» там, где диск промолчал, значит обманывать
/// в единственном вопросе, ради которого сюда и заходят.
/// </remarks>
public sealed class DiskHealthReader
{
    private const string StorageNamespace = @"root\Microsoft\Windows\Storage";

    /// <summary>Прочитать состояние всех физических дисков.</summary>
    public IReadOnlyList<DiskHealth> Read(CancellationToken ct = default)
    {
        var result = new List<DiskHealth>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(StorageNamespace),
                new ObjectQuery("SELECT * FROM MSFT_PhysicalDisk"));

            foreach (var item in searcher.Get())
            {
                ct.ThrowIfCancellationRequested();

                using var disk = (ManagementObject)item;
                result.Add(ReadDisk(disk, ct));
            }
        }
        catch (ManagementException)
        {
            // Подсистема хранения может быть недоступна на урезанных сборках Windows.
        }
        catch (UnauthorizedAccessException)
        {
            // Без повышенных прав часть сведений не отдаётся.
        }

        return result;
    }

    private static DiskHealth ReadDisk(ManagementObject disk, CancellationToken ct)
    {
        var unavailable = new List<string>();

        var model = GetString(disk, "FriendlyName") ?? GetString(disk, "Model") ?? "Неизвестная модель";
        var size = GetLong(disk, "Size") ?? 0;
        var mediaType = DescribeMediaType(GetUInt16(disk, "MediaType"));
        var health = DescribeHealth(GetUInt16(disk, "HealthStatus"));

        if (health == DiskHealthStatus.Unknown)
        {
            unavailable.Add("общий вердикт о состоянии");
        }

        int? temperature = null;
        int? wear = null;
        long? powerOnHours = null;
        long? readErrors = null;

        try
        {
            // Счётчики надёжности лежат в отдельном связанном объекте
            // и доступны не для каждого накопителя.
            using var counters = disk.GetRelated("MSFT_StorageReliabilityCounter");

            var counter = counters.Cast<ManagementObject>().FirstOrDefault();

            if (counter is null)
            {
                unavailable.Add("счётчики надёжности (диск их не сообщает)");
            }
            else
            {
                using (counter)
                {
                    ct.ThrowIfCancellationRequested();

                    temperature = (int?)GetUInt16(counter, "Temperature");
                    wear = (int?)GetUInt16(counter, "Wear");
                    powerOnHours = GetLong(counter, "PowerOnHours");
                    readErrors = GetLong(counter, "ReadErrorsTotal");

                    if (temperature is null or 0)
                    {
                        temperature = null;
                        unavailable.Add("температура");
                    }

                    if (powerOnHours is null or 0)
                    {
                        powerOnHours = null;
                        unavailable.Add("часы работы");
                    }

                    // Износ осмыслен только для твердотельных: у дисков с пластинами
                    // это поле либо отсутствует, либо не значит ничего.
                    if (wear is null && mediaType.Contains("твердотельный", StringComparison.OrdinalIgnoreCase))
                    {
                        unavailable.Add("износ");
                    }
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException)
        {
            unavailable.Add("счётчики надёжности (нет доступа)");
        }

        return new DiskHealth(model, mediaType, size, health, temperature, wear, powerOnHours, readErrors, unavailable);
    }

    private static string DescribeMediaType(ushort? mediaType) => mediaType switch
    {
        3 => "жёсткий диск",
        4 => "твердотельный",
        5 => "гибридный",
        _ => "тип неизвестен",
    };

    private static DiskHealthStatus DescribeHealth(ushort? status) => status switch
    {
        0 => DiskHealthStatus.Healthy,
        1 => DiskHealthStatus.Warning,
        2 => DiskHealthStatus.Unhealthy,
        _ => DiskHealthStatus.Unknown,
    };

    private static string? GetString(ManagementBaseObject source, string property)
    {
        try
        {
            return source[property] as string;
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    private static long? GetLong(ManagementBaseObject source, string property)
    {
        try
        {
            return source[property] switch
            {
                ulong value => (long)value,
                long value => value,
                uint value => value,
                int value => value,
                _ => null,
            };
        }
        catch (ManagementException)
        {
            return null;
        }
    }

    private static ushort? GetUInt16(ManagementBaseObject source, string property)
    {
        try
        {
            return source[property] switch
            {
                ushort value => value,
                int value => (ushort)value,
                uint value => (ushort)value,
                _ => null,
            };
        }
        catch (ManagementException)
        {
            return null;
        }
    }
}
