using Vacate.Abstractions.Model;

namespace Vacate.Platform.Windows.Files;

/// <summary>
/// Превращение находок анализа диска в план удаления.
/// </summary>
/// <remarks>
/// Здесь удаляются ЛИЧНЫЕ файлы человека, а не временные. Разница определяет два решения,
/// отличающие этот построитель от остальных:
///
///   1. Корзина, а не карантин. Карантин — служебное хранилище, о котором человек знает
///      только со слов программы; Корзина — место, куда он привык заглядывать сам
///      и откуда вернёт файл без нашей помощи, даже если программа к тому времени удалена.
///   2. Никогда не зелёный уровень. Даже одинаковая копия — это чей-то файл,
///      а одинаковость установлена нами, а не подтверждена владельцем.
///
/// Из группы одинаковых копий первая не удаляется никогда. Предложить удалить все
/// экземпляры значит превратить освобождение места в потерю данных.
/// </remarks>
public sealed class DiskCleanupPlanBuilder
{
    /// <summary>Собрать план удаления отдельных файлов.</summary>
    public MutationPlan ForFiles(IReadOnlyList<ScannedFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        return Build("disk:files",
            LocalizedText.FromTranslations(new Dictionary<string, string>
            {
                ["ru"] = "Крупные файлы",
                ["en"] = "Large files",
            }),
            files);
    }

    /// <summary>
    /// Собрать план удаления лишних копий.
    /// </summary>
    /// <remarks>
    /// В каждой группе остаётся первый файл. Он же показан человеку как тот,
    /// который сохранится, — до нажатия, а не после.
    /// </remarks>
    public MutationPlan ForDuplicates(IReadOnlyList<DuplicateGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);

        var extras = groups.SelectMany(g => g.Files.Skip(1)).ToList();

        return Build("disk:duplicates",
            LocalizedText.FromTranslations(new Dictionary<string, string>
            {
                ["ru"] = "Лишние копии одинаковых файлов",
                ["en"] = "Duplicate copies",
            }),
            extras);
    }

    private static MutationPlan Build(string groupId, LocalizedText title, IReadOnlyList<ScannedFile> files)
    {
        var operations = new List<PlannedOperation>();
        var totalSize = 0L;
        var index = 0;

        foreach (var file in files)
        {
            if (!File.Exists(file.Path))
            {
                // Файл мог исчезнуть, пока человек читал список. Это не ошибка.
                continue;
            }

            var inCloud = file.Traits.HasFlag(FileTraits.InCloudFolder);

            operations.Add(new DeleteFileOperation
            {
                Id = $"{groupId}:{index++}",
                GroupId = groupId,
                Target = new FileTarget(file.Path, IsDirectory: false, file.SizeOnDiskBytes, file.Traits),

                // Корзина: человек вернёт файл сам, без программы и без наших объяснений.
                Disposition = DeleteDisposition.RecycleBin,

                // Файл в синхронизируемой папке исчезнет на всех устройствах,
                // и Корзина на этом компьютере вернёт его только здесь.
                DeclaredRisk = inCloud ? RiskLevel.Red : RiskLevel.Yellow,

                Consequence = LocalizedText.FromTranslations(new Dictionary<string, string>
                {
                    ["ru"] = inCloud
                        ? $"{file.Path} лежит в синхронизируемой папке: файл исчезнет на всех ваших устройствах, а Корзина вернёт его только здесь."
                        : $"{file.Path} отправится в Корзину. Вернуть можно оттуда.",

                    ["en"] = inCloud
                        ? $"{file.Path} is inside a synced folder: it will disappear on every device, and the Recycle Bin only restores it here."
                        : $"{file.Path} goes to the Recycle Bin.",
                }),
            });

            totalSize += file.SizeOnDiskBytes;
        }

        var group = new OperationGroup
        {
            GroupId = groupId,
            Title = title,
            Operations = operations,
            SizeOnDiskBytes = totalSize,
        };

        return new MutationPlan
        {
            PlanId = $"{groupId}-{Guid.NewGuid():N}",
            Origin = "DiskCleanupPlanBuilder",
            Groups = operations.Count > 0 ? [group] : [],
        };
    }
}
