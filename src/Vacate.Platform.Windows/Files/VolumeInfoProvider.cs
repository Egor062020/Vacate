using Vacate.Abstractions.Execution;

namespace Vacate.Platform.Windows.Files;

/// <summary>
/// Сведения о томах: свободное место и принадлежность путей.
/// </summary>
/// <remarks>
/// Основа честного счётчика. Разница свободного места до и после операции — единственная
/// величина, которую можно назвать результатом очистки. Сумма размеров удалённых файлов,
/// которую показывают конкуренты, почти всегда больше: часть файлов держат открытыми
/// работающие программы, часть имеет несколько жёстких ссылок, часть просто не удалилась.
/// </remarks>
public sealed class VolumeInfoProvider : IVolumeInfoProvider
{
    public IReadOnlyDictionary<string, long> GetFreeSpaceByVolume()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady)
                {
                    result[drive.RootDirectory.FullName] = drive.AvailableFreeSpace;
                }
            }
            catch (IOException)
            {
                // Съёмный носитель могли извлечь между перечислением и опросом.
                // Это не повод срывать всю операцию.
            }
        }

        return result;
    }

    public string GetVolumeRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetPathRoot(Path.GetFullPath(path)) ?? string.Empty;
    }
}
