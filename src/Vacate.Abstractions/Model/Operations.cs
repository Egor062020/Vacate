using System.Text.Json.Serialization;

namespace Vacate.Abstractions.Model;

/// <summary>
/// Одно намерение изменить систему.
/// </summary>
/// <remarks>
/// Единственная модель операции в продукте. Умеет четыре вещи, и это её смысл:
/// показать себя в предпросмотре, быть выполненной, записаться в журнал и быть отменённой.
///
/// Дискриминаторы сериализации закреплены строками и покрыты тестом: если переименовать
/// тип операции, журнал прошлых сессий должен продолжать читаться, иначе откат
/// после обновления программы перестанет работать.
/// </remarks>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(DeleteFileOperation), "file.delete")]
[JsonDerivedType(typeof(DeleteRegistryOperation), "registry.delete")]
[JsonDerivedType(typeof(SetRegistryValueOperation), "registry.set")]
[JsonDerivedType(typeof(EmptyRecycleBinOperation), "recyclebin.empty")]
public abstract record PlannedOperation
{
    /// <summary>Устойчивый идентификатор в пределах плана.</summary>
    public required string Id { get; init; }

    /// <summary>
    /// Нижняя граница риска, заявленная источником операции (правилом очистки).
    /// Охрана может повысить уровень, но не понизить.
    /// </summary>
    public required RiskLevel DeclaredRisk { get; init; }

    /// <summary>
    /// Что произойдёт, человеческим языком. Не «удалить Cookies», а «вы выйдете
    /// из аккаунтов на сайтах». Показывается до нажатия, а не после.
    /// </summary>
    public required LocalizedText Consequence { get; init; }

    /// <summary>Идентификатор группы: правило, каталог или программа, породившая операцию.</summary>
    public required string GroupId { get; init; }
}

/// <summary>Удаление файла или каталога.</summary>
public sealed record DeleteFileOperation : PlannedOperation
{
    public required FileTarget Target { get; init; }

    /// <summary>Способ удаления, определяющий возможность отката.</summary>
    public required DeleteDisposition Disposition { get; init; }
}

/// <summary>Удаление значения или ключа реестра.</summary>
public sealed record DeleteRegistryOperation : PlannedOperation
{
    public required RegistryTarget Target { get; init; }
}

/// <summary>Запись значения реестра (используется при отключении автозапуска и при откате).</summary>
public sealed record SetRegistryValueOperation : PlannedOperation
{
    public required RegistryTarget Target { get; init; }

    /// <summary>Значение в виде, пригодном для сериализации в журнал.</summary>
    public required RegistryValueData Value { get; init; }
}

/// <summary>
/// Очистка Корзины.
/// </summary>
/// <remarks>
/// Всегда отдельная операция и никогда в одном пакете с удалением в Корзину.
/// Иначе один запуск очистки сначала отправит файлы в Корзину, затем очистит её,
/// и кнопка отката останется активной, но работать не будет.
/// </remarks>
public sealed record EmptyRecycleBinOperation : PlannedOperation
{
    /// <summary>Том, чья корзина очищается.</summary>
    public required string VolumeRoot { get; init; }
}

/// <summary>Содержимое значения реестра в переносимом виде.</summary>
/// <param name="Kind">Тип значения.</param>
/// <param name="Data">Данные в кодировке Base64 — так переживают сериализацию любые типы.</param>
public sealed record RegistryValueData(RegistryValueKind Kind, string Data);

/// <summary>Тип значения реестра. Собственный, чтобы слой контрактов не зависел от системного.</summary>
public enum RegistryValueKind
{
    String,
    ExpandString,
    Binary,
    DWord,
    QWord,
    MultiString,
}
