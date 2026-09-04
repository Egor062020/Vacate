namespace PurgeX.Abstractions.Model;

/// <summary>
/// План изменений: всё, что будет сделано за один запуск.
/// </summary>
/// <remarks>
/// План передаётся исполнителю целиком, а не по одной операции. Это сделано ради охраны:
/// дорогие проверки выполняются один раз на группу, а не на каждый из сотен тысяч файлов.
/// Поэлементный шлюз оказался бы неприемлемо медленным, и первый же замер производительности
/// подтолкнул бы обойти его — дверь, мимо которой выгодно ходить, дверью не является.
///
/// Обратите внимание: в плане нет признака «это сухой прогон». Режим предпросмотра
/// задаётся подменой приёмника действий при сборке зависимостей, и исполнитель не имеет
/// способа узнать, в каком режиме он работает. Флаг, который каждый исполнитель обязан
/// не забыть проверить, рано или поздно забудут — и предпросмотр удалит файлы по-настоящему.
/// </remarks>
public sealed record MutationPlan
{
    public required string PlanId { get; init; }

    /// <summary>Группы операций. Группа — это правило очистки, каталог или программа.</summary>
    public required IReadOnlyList<OperationGroup> Groups { get; init; }

    /// <summary>Кто создал план: имя модуля. Попадает в журнал.</summary>
    public required string Origin { get; init; }

    /// <summary>Все операции плана подряд.</summary>
    public IEnumerable<PlannedOperation> AllOperations => Groups.SelectMany(g => g.Operations);

    /// <summary>Суммарное занимаемое место всех целей плана.</summary>
    public long TotalSizeOnDiskBytes => Groups.Sum(g => g.SizeOnDiskBytes);

    /// <summary>Общее число операций.</summary>
    public int TotalCount => Groups.Sum(g => g.Operations.Count);

    /// <summary>Наибольший заявленный уровень риска в плане.</summary>
    public RiskLevel MaxDeclaredRisk =>
        Groups.Count == 0 ? RiskLevel.Green : Groups.Max(g => g.MaxDeclaredRisk);
}

/// <summary>
/// Группа операций с общим происхождением.
/// </summary>
/// <remarks>
/// Группа — единица дешёвой проверки и единица записи в журнал. Журналирование по каждому
/// файлу для очистки на двести тысяч объектов дало бы сотни тысяч записей и часы работы
/// с диском; поэлементно хранится только то, что реально можно вернуть.
/// </remarks>
public sealed record OperationGroup
{
    public required string GroupId { get; init; }

    /// <summary>Название для пользователя: «Кэш Chrome», «Остатки Adobe Reader».</summary>
    public required LocalizedText Title { get; init; }

    /// <summary>Общий корневой путь группы, если он есть. Канонизируется один раз на группу.</summary>
    public string? RootPath { get; init; }

    public required IReadOnlyList<PlannedOperation> Operations { get; init; }

    /// <summary>Занимаемое место всех целей группы.</summary>
    public long SizeOnDiskBytes { get; init; }

    public RiskLevel MaxDeclaredRisk =>
        Operations.Count == 0 ? RiskLevel.Green : Operations.Max(o => o.DeclaredRisk);
}
