namespace PurgeX.Abstractions.Model;

/// <summary>
/// Корневой раздел реестра. Собственный тип, а не системный, чтобы слой контрактов
/// не тянул за собой доступ к реестру.
/// </summary>
public enum RegistryHiveKind
{
    LocalMachine,
    CurrentUser,
    Users,
}

/// <summary>
/// Разрядность представления реестра.
/// </summary>
/// <remarks>
/// Обязательная часть адреса ключа, а не необязательная деталь. Потеря этого значения
/// даёт классическую ошибку «удалили не ту ветку»: 32-разрядные программы в 64-разрядной
/// Windows видят собственное представление того же пути.
/// </remarks>
public enum RegistryViewKind
{
    /// <summary>Разрядность процесса. Продукт собирается только под x64, значит 64-разрядное.</summary>
    Default,

    /// <summary>Представление для 32-разрядных программ.</summary>
    Registry32,

    /// <summary>64-разрядное представление.</summary>
    Registry64,
}

/// <summary>
/// Файл или каталог как цель операции.
/// </summary>
/// <param name="Path">Полный путь. Приведён к каноническому виду охраной перед выполнением.</param>
/// <param name="IsDirectory">Каталог, а не файл.</param>
/// <param name="SizeOnDiskBytes">
/// Занимаемое место на диске, а не логический размер. Для сжатых, разреженных файлов
/// и облачных заглушек эти величины различаются в разы, и счётчик освобождённого
/// обязан считать именно занимаемое место.
/// </param>
/// <param name="Traits">Особенности объекта, влияющие на безопасность операции.</param>
public sealed record FileTarget(
    string Path,
    bool IsDirectory,
    long SizeOnDiskBytes,
    FileTraits Traits);

/// <summary>
/// Свойства файла, из-за которых с ним нельзя обращаться как с обычным.
/// </summary>
[Flags]
public enum FileTraits
{
    None = 0,

    /// <summary>
    /// Точка повторной обработки: соединение каталогов или символическая ссылка.
    /// Обход по таким объектам не идёт никогда без явного разрешения — иначе поиск дубликатов
    /// покажет один файл как две копии, и пользователь удалит единственный экземпляр.
    /// </summary>
    ReparsePoint = 1 << 0,

    /// <summary>Файл имеет больше одной жёсткой ссылки: удаление одной не освобождает место.</summary>
    MultipleHardLinks = 1 << 1,

    /// <summary>
    /// Облачная заглушка: содержимое физически не скачано. Такие файлы нельзя открывать
    /// (иначе программа сама скачает гигабайты) и нельзя хешировать.
    /// </summary>
    CloudPlaceholder = 1 << 2,

    /// <summary>Только для чтения. Атрибут снимается перед удалением, иначе система откажет.</summary>
    ReadOnly = 1 << 3,

    /// <summary>Лежит внутри каталога, синхронизируемого облачным хранилищем.</summary>
    InCloudFolder = 1 << 4,

    /// <summary>Файл сжат или разрежен: логический размер больше занимаемого места.</summary>
    CompressedOrSparse = 1 << 5,
}

/// <summary>
/// Значение или ключ реестра как цель операции.
/// </summary>
/// <param name="Hive">Корневой раздел.</param>
/// <param name="SubKeyPath">Путь ключа внутри раздела.</param>
/// <param name="ValueName">
/// Имя значения. Пустая ссылка означает, что целью является сам ключ целиком.
/// </param>
/// <param name="View">Разрядность представления. Обязательна.</param>
/// <param name="UserSid">
/// Идентификатор пользователя для раздела <see cref="RegistryHiveKind.Users"/>.
/// Работа ведётся именно с ним, а не с «текущим пользователем»: при запуске с правами
/// другой учётной записи «текущий» указывал бы на чужой профиль.
/// </param>
public sealed record RegistryTarget(
    RegistryHiveKind Hive,
    string SubKeyPath,
    string? ValueName,
    RegistryViewKind View,
    string? UserSid = null)
{
    /// <summary>Целью является ключ целиком, а не отдельное значение.</summary>
    public bool IsWholeKey => ValueName is null;
}
