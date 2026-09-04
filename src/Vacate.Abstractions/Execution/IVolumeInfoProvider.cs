namespace Vacate.Abstractions.Execution;

/// <summary>
/// Сведения о томах. Нужны честному счётчику освобождённого места.
/// </summary>
public interface IVolumeInfoProvider
{
    /// <summary>
    /// Свободное место по всем томам.
    /// </summary>
    /// <remarks>
    /// Замеряется до и после операции. Именно эта разница — настоящий результат очистки,
    /// в отличие от суммы размеров удалённых файлов, которую показывают конкуренты
    /// и которая почти всегда больше действительной.
    /// </remarks>
    IReadOnlyDictionary<string, long> GetFreeSpaceByVolume();

    /// <summary>Корень тома, на котором лежит путь. Например, «C:\».</summary>
    string GetVolumeRoot(string path);

    /// <summary>
    /// Порог, ниже которого включается аварийный режим: страховка на диске уже не помещается.
    /// </summary>
    long EmergencyThresholdBytes => 512L * 1024 * 1024;
}
