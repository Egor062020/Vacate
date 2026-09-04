namespace Vacate.Abstractions.Model;

/// <summary>
/// Остаток удалённой программы: папка или ветка реестра, которую деинсталлятор не убрал.
/// </summary>
/// <param name="Path">Путь к папке или ветке реестра.</param>
/// <param name="Kind">Что это.</param>
/// <param name="SizeOnDiskBytes">Занимаемое место (для файловых остатков).</param>
/// <param name="Confidence">Насколько уверенно объект связан с удалённой программой.</param>
/// <param name="Evidence">
/// На чём основан вывод. Показывается пользователю: он должен видеть причину,
/// а не просто галочку. «Совпало имя программы и издатель» — это довод,
/// «найдено» — нет.
/// </param>
public sealed record LeftoverItem(
    string Path,
    LeftoverKind Kind,
    long SizeOnDiskBytes,
    LeftoverConfidence Confidence,
    IReadOnlyList<string> Evidence);

public enum LeftoverKind
{
    Directory,
    RegistryKey,
}

/// <summary>
/// Насколько объект похож на остаток именно этой программы.
/// </summary>
/// <remarks>
/// Три уровня вместо числовой оценки. Показывать «уверенность 0.86» для вывода,
/// собранного из трёх признаков, — фальшивая точность: пользователь принимает её
/// за измерение, хотя это правило с весами, придуманное разработчиком.
/// </remarks>
public enum LeftoverConfidence
{
    /// <summary>
    /// Путь указан самой программой или лежит внутри её каталога установки.
    /// Можно предлагать к удалению по умолчанию.
    /// </summary>
    Certain,

    /// <summary>
    /// Совпало имя программы. По умолчанию отмечено, но с показом причины.
    /// </summary>
    Likely,

    /// <summary>
    /// Совпал только издатель или частичное имя. По умолчанию НЕ отмечено:
    /// у одного издателя много программ, и его каталог может быть общим.
    /// </summary>
    Possible,
}
