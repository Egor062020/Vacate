namespace Vacate.Abstractions.Model;

/// <summary>
/// Расширение браузера.
/// </summary>
/// <param name="Id">Идентификатор расширения.</param>
/// <param name="Name">Название.</param>
/// <param name="Version">Версия.</param>
/// <param name="Browser">В каком браузере установлено.</param>
/// <param name="ProfileName">Профиль браузера: у людей их бывает несколько.</param>
/// <param name="Permissions">Запрошенные разрешения в понятном виде.</param>
/// <param name="SizeBytes">Занимаемое место.</param>
/// <param name="Path">Каталог расширения.</param>
/// <param name="LastUpdatedUtc">Когда каталог менялся последний раз — косвенный признак заброшенности.</param>
public sealed record BrowserExtension(
    string Id,
    string Name,
    string? Version,
    string Browser,
    string ProfileName,
    IReadOnlyList<ExtensionPermission> Permissions,
    long SizeBytes,
    string Path,
    DateTime? LastUpdatedUtc)
{
    /// <summary>Расширение имеет доступ к содержимому всех открываемых страниц.</summary>
    public bool ReadsAllSites => Permissions.Any(p => p.Level == PermissionLevel.AllSites);

    /// <summary>Наивысший уровень запрошенных прав.</summary>
    public PermissionLevel HighestLevel =>
        Permissions.Count == 0 ? PermissionLevel.Harmless : Permissions.Max(p => p.Level);
}

/// <summary>
/// Разрешение расширения, переведённое на человеческий язык.
/// </summary>
/// <param name="Raw">Как записано в манифесте.</param>
/// <param name="Description">Что это значит для пользователя.</param>
/// <param name="Level">Насколько это серьёзно.</param>
public sealed record ExtensionPermission(string Raw, string Description, PermissionLevel Level);

/// <summary>
/// Насколько серьёзно разрешение.
/// </summary>
/// <remarks>
/// Смысл раздела именно в этом. Список расширений человек и так видит в браузере;
/// чего он не видит — что одно из них читает всё, что он открывает, включая
/// банк и почту, а другое просто рисует кнопку.
/// </remarks>
public enum PermissionLevel
{
    /// <summary>Ничего чувствительного.</summary>
    Harmless = 0,

    /// <summary>Доступ к данным, но ограниченный.</summary>
    Notable = 1,

    /// <summary>Доступ к содержимому конкретных сайтов.</summary>
    SomeSites = 2,

    /// <summary>Чтение и изменение данных на всех сайтах.</summary>
    AllSites = 3,
}
