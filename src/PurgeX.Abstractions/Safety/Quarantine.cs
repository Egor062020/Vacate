using PurgeX.Abstractions.Model;

namespace PurgeX.Abstractions.Safety;

/// <summary>
/// Карантин — основной механизм отката.
/// </summary>
/// <remarks>
/// Устройство продиктовано разбором:
///
/// 1. Карантин размещается НА КАЖДОМ ТОМЕ свой. Перемещение мгновенно только внутри одного
///    тома; файл с диска D: в карантин на C: — это копирование с удвоением занятого места,
///    а на заполненном диске просто отказ.
///
/// 2. Каталоги карантина исключаются из собственных сканирований. Иначе карта диска покажет
///    карантин самой большой папкой, а поиск дубликатов найдёт карантинные копии и предложит
///    удалить оригинал.
///
/// 3. Карантин, как и Корзина, не освобождает место немедленно. Это честно показывается
///    пользователю двумя разными числами: сколько освободилось сейчас и сколько освободится
///    после истечения срока.
/// </remarks>
public interface IQuarantine
{
    /// <summary>Поместить объект в карантин тома, на котором он лежит.</summary>
    Task<QuarantineResult> StoreAsync(FileTarget target, CancellationToken ct);

    /// <summary>
    /// Вернуть объект на исходное место.
    /// </summary>
    /// <remarks>
    /// Проверяется не только появление файла, но и права доступа, атрибуты и метки времени:
    /// «файл на месте» без прежних прав — это не восстановление.
    /// </remarks>
    Task<RestoreResult> RestoreAsync(string undoToken, CancellationToken ct);

    /// <summary>Удалить объекты, чей срок истёк. Вызывается при запуске и по расписанию.</summary>
    Task<int> PurgeExpiredAsync(CancellationToken ct);

    /// <summary>Сколько места занято карантином по томам.</summary>
    Task<IReadOnlyDictionary<string, long>> GetUsageByVolumeAsync(CancellationToken ct);

    /// <summary>Является ли путь частью служебных каталогов карантина.</summary>
    bool IsQuarantinePath(string path);
}

/// <param name="Success">Удалось ли поместить в карантин.</param>
/// <param name="UndoToken">Идентификатор для возврата.</param>
/// <param name="Reason">Почему не удалось.</param>
/// <param name="BudgetExceeded">
/// Место в карантине исчерпано. Тихого вытеснения старых записей не происходит никогда:
/// пользователь получает выбор — отказаться или выполнить операцию без возможности отката.
/// </param>
public sealed record QuarantineResult(
    bool Success,
    string? UndoToken = null,
    LocalizedText? Reason = null,
    bool BudgetExceeded = false);

/// <param name="Success">Объект возвращён на место.</param>
/// <param name="AttributesRestored">Восстановлены права доступа и атрибуты, а не только содержимое.</param>
/// <param name="Reason">Что помешало.</param>
public sealed record RestoreResult(bool Success, bool AttributesRestored = false, LocalizedText? Reason = null);
