namespace PurgeX.Abstractions.Model;

/// <summary>
/// Уровень риска действия. Ровно три значения.
/// </summary>
/// <remarks>
/// Запрет намеренно НЕ является уровнем риска: иначе интерфейс покажет пользователю
/// кнопку удержания на действии, которое всё равно откажет. Запрет выражается
/// вердиктом охраны (<see cref="Safety.GuardVerdict"/>), а не четвёртым значением здесь.
/// </remarks>
public enum RiskLevel
{
    /// <summary>Безопасно, можно выполнять без церемоний.</summary>
    Green = 0,

    /// <summary>Есть последствия, их нужно назвать пользователю до нажатия.</summary>
    Yellow = 1,

    /// <summary>Опасно: требуется осознанное подтверждение удержанием кнопки.</summary>
    Red = 2,
}

/// <summary>
/// Как именно удаляется объект. От этого зависит, возможен ли откат.
/// </summary>
public enum DeleteDisposition
{
    /// <summary>
    /// В карантин: объект перемещается в служебный каталог на том же томе и может быть возвращён.
    /// Основной механизм отката.
    /// </summary>
    Quarantine = 0,

    /// <summary>
    /// В Корзину. Только для документоподобных файлов (загрузки, дистрибутивы, дубликаты).
    /// Не используется для временных файлов: Корзина имеет квоту, при переполнении которой
    /// Windows стирает файл молча и безвозвратно.
    /// </summary>
    RecycleBin = 1,

    /// <summary>
    /// Безвозвратно. Допустимо только для того, что создаётся заново (кэши, временные файлы),
    /// и только когда пользователю честно сказано, что восстановления не будет.
    /// </summary>
    Permanent = 2,
}

/// <summary>
/// Текст, который увидит пользователь.
/// </summary>
/// <remarks>
/// Хранится в виде ключа ресурса, а не готовой строки, чтобы журнал операций переживал
/// смену языка интерфейса: запись, сделанная по-русски, должна читаться по-английски
/// после переключения. Для текстов, приходящих из правил очистки (они не в ресурсах сборки),
/// предусмотрен словарь переводов.
/// </remarks>
public sealed record LocalizedText
{
    private LocalizedText(string? resourceKey, IReadOnlyDictionary<string, string>? translations, object[] args)
    {
        ResourceKey = resourceKey;
        Translations = translations;
        Args = args;
    }

    /// <summary>Ключ строки в ресурсах сборки. Задан, если текст встроенный.</summary>
    public string? ResourceKey { get; }

    /// <summary>Переводы по коду языка. Задан, если текст пришёл из правила очистки.</summary>
    public IReadOnlyDictionary<string, string>? Translations { get; }

    /// <summary>Подстановки для форматирования.</summary>
    public object[] Args { get; }

    /// <summary>Текст из ресурсов сборки.</summary>
    public static LocalizedText FromResource(string resourceKey, params object[] args)
        => new(resourceKey, null, args);

    /// <summary>Текст из правила очистки, где переводы заданы прямо в файле правила.</summary>
    public static LocalizedText FromTranslations(IReadOnlyDictionary<string, string> translations, params object[] args)
        => new(null, translations, args);
}
