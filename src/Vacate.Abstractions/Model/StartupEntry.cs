namespace Vacate.Abstractions.Model;

/// <summary>
/// Запись автозапуска: то, что стартует вместе с Windows.
/// </summary>
/// <param name="Id">Устойчивый идентификатор в пределах источника.</param>
/// <param name="Name">Название для пользователя.</param>
/// <param name="Command">Команда запуска целиком.</param>
/// <param name="ImagePath">Путь к исполняемому файлу, если его удалось выделить.</param>
/// <param name="Source">Откуда запускается.</param>
/// <param name="Scope">Для всех пользователей или только для текущего.</param>
/// <param name="IsEnabled">Включено сейчас.</param>
/// <param name="Control">Что с этой записью вообще можно делать.</param>
/// <param name="Publisher">Издатель по цифровой подписи или сведениям о файле.</param>
/// <param name="Note">
/// Пояснение для пользователя: почему запись нельзя трогать, что сломается при отключении.
/// </param>
public sealed record StartupEntry(
    string Id,
    string Name,
    string Command,
    string? ImagePath,
    StartupSource Source,
    InstallScope Scope,
    bool IsEnabled,
    StartupControl Control,
    string? Publisher = null,
    string? Note = null);

/// <summary>Откуда запускается запись.</summary>
public enum StartupSource
{
    /// <summary>Ключ Run в реестре.</summary>
    RunKey,

    /// <summary>Папка автозагрузки.</summary>
    StartupFolder,

    /// <summary>Задача планировщика.</summary>
    ScheduledTask,

    /// <summary>Служба Windows.</summary>
    Service,
}

/// <summary>
/// Что можно делать с записью.
/// </summary>
/// <remarks>
/// Уровень определяется заранее и показывается в интерфейсе неактивной кнопкой
/// с пояснением, а не ошибкой после нажатия. Пользователь не должен узнавать
/// о запрете, уже нажав на него.
/// </remarks>
public enum StartupControl
{
    /// <summary>Можно включать и отключать.</summary>
    Toggleable,

    /// <summary>
    /// Только просмотр: критичная системная служба, защита Windows или задача,
    /// которую система всё равно пересоздаст.
    /// </summary>
    ViewOnly,
}
