namespace Vacate.Abstractions.Model;

/// <summary>
/// Установленная программа.
/// </summary>
/// <param name="Id">Имя ключа реестра — устойчивый идентификатор.</param>
/// <param name="DisplayName">Название для пользователя.</param>
/// <param name="Version">Версия.</param>
/// <param name="Publisher">Издатель.</param>
/// <param name="InstallLocation">Каталог установки, если программа его указала.</param>
/// <param name="UninstallCommand">Команда удаления.</param>
/// <param name="QuietUninstallCommand">Команда удаления без вопросов, если программа её объявила.</param>
/// <param name="InstallDate">Дата установки.</param>
/// <param name="EstimatedSizeBytes">
/// Размер по заявлению самой программы. Величина ненадёжна: многие занижают её или не указывают
/// вовсе, поэтому в интерфейсе рядом должен идти фактически посчитанный размер каталога.
/// </param>
/// <param name="Scope">Для всех пользователей или только для текущего.</param>
/// <param name="Is32BitOnWin64">Запись лежит в 32-разрядном представлении реестра.</param>
/// <param name="IconPath">Путь к значку для показа настоящей иконки программы.</param>
public sealed record InstalledApp(
    string Id,
    string DisplayName,
    string? Version,
    string? Publisher,
    string? InstallLocation,
    string? UninstallCommand,
    string? QuietUninstallCommand,
    DateOnly? InstallDate,
    long EstimatedSizeBytes,
    InstallScope Scope,
    bool Is32BitOnWin64,
    string? IconPath)
{
    /// <summary>Программу можно удалить штатным способом.</summary>
    public bool CanUninstall => !string.IsNullOrWhiteSpace(UninstallCommand);

    /// <summary>
    /// Похоже на распространяемый компонент, нужный другим программам.
    /// </summary>
    /// <remarks>
    /// Такие записи занимают заметную часть списка у обычного пользователя, и удалять их
    /// вслепую — верный способ сломать работающие программы. В интерфейсе они помечаются
    /// отдельно, а не предлагаются к удалению наравне с остальными.
    /// </remarks>
    public bool LooksLikeRuntime =>
        DisplayName.Contains("Visual C++", StringComparison.OrdinalIgnoreCase)
        || DisplayName.Contains("Redistributable", StringComparison.OrdinalIgnoreCase)
        || DisplayName.Contains(".NET Runtime", StringComparison.OrdinalIgnoreCase)
        || DisplayName.Contains(".NET Desktop Runtime", StringComparison.OrdinalIgnoreCase)
        || DisplayName.Contains("DirectX", StringComparison.OrdinalIgnoreCase)
        || DisplayName.Contains("Microsoft Edge WebView", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Для кого установлена программа.</summary>
public enum InstallScope
{
    /// <summary>Для всех пользователей компьютера.</summary>
    Machine,

    /// <summary>Только для одного пользователя.</summary>
    User,
}
