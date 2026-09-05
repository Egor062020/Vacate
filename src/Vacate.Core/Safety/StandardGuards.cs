using Vacate.Abstractions.Model;
using Vacate.Abstractions.Safety;

namespace Vacate.Core.Safety;

/// <summary>
/// Полный набор охраны. Единственное место, где перечислены все проверки.
/// </summary>
/// <remarks>
/// Раньше список повторялся в каждом месте сборки исполнителя. Пока проверок было
/// четыре и модулей два, это выглядело безобидно — а на деле означало, что новая
/// проверка защищает ровно те вызовы, где её не забыли дописать. Забыть в одном
/// из четырёх мест достаточно, чтобы дыра выглядела закрытой и не была.
/// </remarks>
public static class GuardSet
{
    /// <summary>Дешёвая охрана уровня группы.</summary>
    public static IReadOnlyList<IGroupGuard> Group(PathPolicy policy) =>
    [
        new EmergencyModeGuard(),
        new ProtectedPathGuard(policy),
        new ProtectedRegistryGuard(),
        new RecycleBinOrderGuard(),
        new VolumeLimitGuard(),
    ];

    /// <summary>Дорогая охрана уровня объекта: применяется только к жёлтому и красному.</summary>
    public static IReadOnlyList<IItemGuard> Item() => [new ReparseAndCloudGuard()];
}

/// <summary>
/// Проверка защищённых путей. Дешёвая, работает на уровне группы.
/// </summary>
public sealed class ProtectedPathGuard(PathPolicy policy) : IGroupGuard
{
    public int Order => 10;
    public GuardScope Scope => GuardScope.Group;
    public string Name => "Защищённые пути";

    public GuardVerdict Evaluate(OperationGroup group, GuardEnvironment environment)
    {
        var caution = false;
        string? cautionReason = null;

        foreach (var operation in group.Operations)
        {
            if (operation is not DeleteFileOperation delete)
            {
                continue;
            }

            var decision = policy.Evaluate(delete.Target.Path);

            if (!decision.IsAllowed)
            {
                return GuardVerdict.Deny(LocalizedText.FromResource(
                    "Guard.ProtectedPath.Denied",
                    delete.Target.Path,
                    decision.Reason ?? string.Empty));
            }

            if (decision.RequiresCaution)
            {
                caution = true;
                cautionReason ??= decision.Reason;
            }
        }

        return caution
            ? GuardVerdict.Raise(RiskLevel.Yellow, LocalizedText.FromResource("Guard.ProtectedPath.Caution", cautionReason ?? string.Empty))
            : GuardVerdict.Allow();
    }
}

/// <summary>
/// Защита критичных ветвей реестра. Дешёвая, работает на уровне группы.
/// </summary>
/// <remarks>
/// Появилась вместе с удалением остатков программ и закрывает несимметричность,
/// которую раньше не было видно: пути файлов проверял <see cref="ProtectedPathGuard"/>,
/// а ветки реестра не проверял никто. Пока единственным источником таких операций был
/// поиск остатков со своим списком исключений, это сходило с рук — но шлюз, полагающийся
/// на добросовестность вызывающего, шлюзом не является. Достаточно одного нового модуля,
/// который забудет про исключения, чтобы снести ветку, ломающую вход в систему.
///
/// Проверка идёт посегментно, а не по префиксу строки: иначе «SOFTWARE\MicrosoftEdgeBackup»
/// считался бы находящимся внутри «SOFTWARE\Microsoft».
/// </remarks>
public sealed class ProtectedRegistryGuard : IGroupGuard
{
    /// <summary>Ветви, удаление которых не может иметь законной причины.</summary>
    private static readonly string[] Protected =
    [
        @"SOFTWARE\Microsoft",
        @"SOFTWARE\Classes",
        @"SOFTWARE\Policies",
        @"SOFTWARE\WOW6432Node\Microsoft",
        @"SOFTWARE\Windows",
        "SYSTEM",
        "SECURITY",
        "SAM",
        "HARDWARE",
        "BCD00000000",
    ];

    /// <summary>
    /// Узлы, которые нельзя удалять целиком, но внутрь которых ходить можно.
    /// </summary>
    private static readonly string[] NeverWholeKey =
    [
        "SOFTWARE",
        @"SOFTWARE\WOW6432Node",
        "Software",
    ];

    public int Order => 12;
    public GuardScope Scope => GuardScope.Group;
    public string Name => "Защищённые ветви реестра";

    public GuardVerdict Evaluate(OperationGroup group, GuardEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(group);

        foreach (var operation in group.Operations)
        {
            var target = operation switch
            {
                DeleteRegistryOperation delete => delete.Target,
                _ => null,
            };

            if (target is null)
            {
                continue;
            }

            var path = target.SubKeyPath.Trim('\\');

            if (string.IsNullOrEmpty(path))
            {
                return GuardVerdict.Deny(LocalizedText.FromResource("Guard.Registry.WholeHive"));
            }

            if (NeverWholeKey.Any(k => string.Equals(path, k, StringComparison.OrdinalIgnoreCase)))
            {
                return GuardVerdict.Deny(LocalizedText.FromResource("Guard.Registry.WholeKey", path));
            }

            if (Protected.Any(p => IsSameOrInside(path, p)))
            {
                return GuardVerdict.Deny(LocalizedText.FromResource("Guard.Registry.Protected", path));
            }
        }

        return GuardVerdict.Allow();
    }

    /// <summary>Лежит ли ключ внутри другого или совпадает с ним. Сравнение посегментное.</summary>
    internal static bool IsSameOrInside(string path, string container)
    {
        if (string.Equals(path, container, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.StartsWith(container + '\\', StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Предохранитель от массовой катастрофы: слишком большой объём одной операции.
/// </summary>
/// <remarks>
/// Не запрещает, а поднимает риск до красного: пользователь должен увидеть
/// «будет удалено 34 ГБ и 12 431 файл, проверьте список» и подтвердить удержанием кнопки.
/// </remarks>
public sealed class VolumeLimitGuard : IGroupGuard
{
    /// <summary>Порог по объёму, после которого нужно осознанное подтверждение.</summary>
    public const long LargeOperationBytes = 20L * 1024 * 1024 * 1024;

    /// <summary>Порог по числу объектов.</summary>
    public const int LargeOperationCount = 50_000;

    public int Order => 20;
    public GuardScope Scope => GuardScope.Group;
    public string Name => "Ограничение объёма";

    public GuardVerdict Evaluate(OperationGroup group, GuardEnvironment environment)
    {
        if (group.SizeOnDiskBytes >= LargeOperationBytes)
        {
            return GuardVerdict.Raise(
                RiskLevel.Red,
                LocalizedText.FromResource("Guard.Volume.TooLarge", group.SizeOnDiskBytes));
        }

        if (group.Operations.Count >= LargeOperationCount)
        {
            return GuardVerdict.Raise(
                RiskLevel.Red,
                LocalizedText.FromResource("Guard.Volume.TooManyItems", group.Operations.Count));
        }

        return GuardVerdict.Allow();
    }
}

/// <summary>
/// Ограничения аварийного режима при нехватке места на диске.
/// </summary>
/// <remarks>
/// Главный сценарий продукта — «кончается место», и именно тогда отказывает вся страховка:
/// журнал, карантин, точка восстановления. Правило «нет отката — не выполняем» превратилось бы
/// в «программа отказывается работать ровно тогда, когда нужна».
///
/// Поэтому в аварийном режиме разрешены только операции над тем, что создаётся заново,
/// и только безвозвратные — карантин всё равно недоступен. Всё остальное отклоняется явно,
/// с понятным объяснением, а не непонятной ошибкой.
/// </remarks>
public sealed class EmergencyModeGuard : IGroupGuard
{
    public int Order => 5;
    public GuardScope Scope => GuardScope.Group;
    public string Name => "Аварийный режим";

    public GuardVerdict Evaluate(OperationGroup group, GuardEnvironment environment)
    {
        if (!environment.IsEmergencyMode)
        {
            return GuardVerdict.Allow();
        }

        foreach (var operation in group.Operations)
        {
            switch (operation)
            {
                case DeleteFileOperation { Disposition: DeleteDisposition.Quarantine }:
                    return GuardVerdict.Deny(LocalizedText.FromResource("Guard.Emergency.NoQuarantine"));

                case DeleteRegistryOperation or SetRegistryValueOperation:
                    return GuardVerdict.Deny(LocalizedText.FromResource("Guard.Emergency.NoRegistry"));
            }
        }

        return GuardVerdict.Raise(RiskLevel.Yellow, LocalizedText.FromResource("Guard.Emergency.NoRollback"));
    }
}

/// <summary>
/// Точки повторной обработки и облачные заглушки: дорогая проверка по каждому объекту.
/// </summary>
/// <remarks>
/// Соединение каталогов — прямой путь к потере данных. Если папка «Документы» перенаправлена
/// на другой диск, поиск дубликатов покажет один и тот же файл как две копии по разным путям,
/// и пользователь удалит «лишнюю», потеряв единственную.
///
/// Облачные заглушки нельзя даже открывать: чтение содержимого заставит систему скачать файл,
/// то есть программа израсходует трафик пользователя и займёт место вместо того, чтобы освободить.
/// </remarks>
public sealed class ReparseAndCloudGuard : IItemGuard
{
    public int Order => 30;
    public GuardScope Scope => GuardScope.Item;
    public string Name => "Ссылки и облачные файлы";

    public GuardVerdict Evaluate(PlannedOperation operation, GuardEnvironment environment)
    {
        if (operation is not DeleteFileOperation delete)
        {
            return GuardVerdict.Allow();
        }

        var traits = delete.Target.Traits;

        if (traits.HasFlag(FileTraits.ReparsePoint))
        {
            return GuardVerdict.Deny(LocalizedText.FromResource("Guard.Reparse.Denied", delete.Target.Path));
        }

        if (traits.HasFlag(FileTraits.CloudPlaceholder))
        {
            return GuardVerdict.Deny(LocalizedText.FromResource("Guard.Cloud.Placeholder", delete.Target.Path));
        }

        if (traits.HasFlag(FileTraits.InCloudFolder))
        {
            // Удаление уйдёт на все устройства пользователя — это должен быть осознанный выбор.
            return GuardVerdict.Raise(RiskLevel.Red, LocalizedText.FromResource("Guard.Cloud.SyncedFolder", delete.Target.Path));
        }

        return GuardVerdict.Allow();
    }
}

/// <summary>
/// Очистка Корзины не должна выполняться в одном пакете с удалением в Корзину.
/// </summary>
/// <remarks>
/// Иначе один запуск быстрой очистки сначала отправит гигабайты в Корзину, следом её очистит,
/// и кнопка отката останется активной, но работать не будет. Это ровно та ситуация,
/// за которую расплачиваются чужими данными.
/// </remarks>
public sealed class RecycleBinOrderGuard : IGroupGuard
{
    public int Order => 15;
    public GuardScope Scope => GuardScope.Group;
    public string Name => "Порядок очистки Корзины";

    public GuardVerdict Evaluate(OperationGroup group, GuardEnvironment environment)
    {
        var emptiesRecycleBin = group.Operations.OfType<EmptyRecycleBinOperation>().Any();
        var movesToRecycleBin = group.Operations
            .OfType<DeleteFileOperation>()
            .Any(o => o.Disposition == DeleteDisposition.RecycleBin);

        return emptiesRecycleBin && movesToRecycleBin
            ? GuardVerdict.Deny(LocalizedText.FromResource("Guard.RecycleBin.MixedBatch"))
            : GuardVerdict.Allow();
    }
}
