using Vacate.Abstractions.Model;

namespace Vacate.Abstractions.Safety;

/// <summary>
/// Решение охраны по поводу операции или группы операций.
/// </summary>
public readonly record struct GuardVerdict
{
    private GuardVerdict(GuardDecision decision, RiskLevel? raiseTo, LocalizedText? reason)
    {
        Decision = decision;
        RaiseRiskTo = raiseTo;
        Reason = reason;
    }

    public GuardDecision Decision { get; }

    /// <summary>До какого уровня поднять риск. Понизить охрана не может никогда.</summary>
    public RiskLevel? RaiseRiskTo { get; }

    /// <summary>Причина отказа или повышения — показывается пользователю.</summary>
    public LocalizedText? Reason { get; }

    /// <summary>Возражений нет.</summary>
    public static GuardVerdict Allow() => new(GuardDecision.Allow, null, null);

    /// <summary>
    /// Запрет. Не уровень риска, а именно отказ: кнопка в интерфейсе неактивна,
    /// и никакое подтверждение пользователя его не снимает.
    /// </summary>
    public static GuardVerdict Deny(LocalizedText reason) => new(GuardDecision.Deny, null, reason);

    /// <summary>Выполнять можно, но уровень риска выше заявленного.</summary>
    public static GuardVerdict Raise(RiskLevel to, LocalizedText reason) => new(GuardDecision.Allow, to, reason);
}

public enum GuardDecision
{
    Allow,
    Deny,
}

/// <summary>
/// Когда работает проверка.
/// </summary>
/// <remarks>
/// Разделение по цене обязательно. Десять тяжёлых проверок на каждый из двухсот тысяч
/// файлов — это часы работы, после которых охрану просто отключат «чтобы работало».
/// </remarks>
public enum GuardScope
{
    /// <summary>
    /// Дешёвая проверка, один раз на группу: зона, запреты, лимиты.
    /// Бюджет — не более 5 мс на группу.
    /// </summary>
    Group,

    /// <summary>
    /// Дорогая проверка по каждому объекту: защищённость системного файла, подпись,
    /// кто держит файл. Применяется только к жёлтым и красным операциям и только
    /// к единичным объектам.
    /// </summary>
    Item,
}

/// <summary>Проверка безопасности.</summary>
public interface IGuard
{
    /// <summary>Порядок применения: чем меньше, тем раньше. Самые дешёвые идут первыми.</summary>
    int Order { get; }

    GuardScope Scope { get; }

    /// <summary>Человекочитаемое имя для журнала и отладки.</summary>
    string Name { get; }
}

/// <summary>Проверка уровня группы.</summary>
public interface IGroupGuard : IGuard
{
    GuardVerdict Evaluate(OperationGroup group, GuardEnvironment environment);
}

/// <summary>Проверка уровня отдельной операции.</summary>
public interface IItemGuard : IGuard
{
    GuardVerdict Evaluate(PlannedOperation operation, GuardEnvironment environment);
}

/// <summary>
/// Сведения об окружении, нужные охране для решения.
/// </summary>
/// <param name="TargetUserSid">
/// Идентификатор пользователя, чей профиль обрабатывается. Не «текущий пользователь»:
/// при запуске с правами другой учётной записи текущим оказался бы администратор,
/// и программа почистила бы чужой профиль, отрапортовав об успехе.
/// </param>
/// <param name="TargetUserProfilePath">Путь профиля этого пользователя.</param>
/// <param name="FreeSpaceByVolume">Свободное место по томам на момент проверки.</param>
/// <param name="IsEmergencyMode">
/// Аварийный режим при нехватке места: карантин и журнал на диске недоступны,
/// разрешены только необратимые операции над тем, что создаётся заново.
/// </param>
/// <param name="AdvancedMode">Продвинутый режим включён пользователем осознанно.</param>
public sealed record GuardEnvironment(
    string TargetUserSid,
    string TargetUserProfilePath,
    IReadOnlyDictionary<string, long> FreeSpaceByVolume,
    bool IsEmergencyMode,
    bool AdvancedMode);
