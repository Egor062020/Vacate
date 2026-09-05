using Vacate.Abstractions.Model;

namespace Vacate.Platform.Windows.Registry;

/// <summary>
/// Превращение выбранных остатков в план изменений.
/// </summary>
/// <remarks>
/// Отдельный класс, а не метод сканера. Сканер ищет и обязан оставаться безобидным:
/// если бы он сам возвращал готовый план, между «нашли» и «удалили» не осталось бы
/// ни одного места, где обязателен выбор человека. Здесь это место есть — построитель
/// принимает не всё найденное, а только отмеченное.
///
/// Уровень риска берётся из уверенности поиска. Остаток уровня «возможно» получает
/// красный: за ним стоит одно лишь совпадение части имени, и цена ошибки — чужой
/// каталог с данными — несопоставима с выигрышем в несколько мегабайт.
/// </remarks>
public sealed class LeftoverPlanBuilder
{
    /// <summary>Собрать план удаления отмеченных остатков.</summary>
    /// <param name="app">Программа, чьи следы удаляются.</param>
    /// <param name="selected">Отмеченные пользователем остатки.</param>
    public MutationPlan Build(InstalledApp app, IReadOnlyList<LeftoverItem> selected)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(selected);

        var groupId = $"leftovers:{app.DisplayName}";
        var operations = new List<PlannedOperation>();
        var totalSize = 0L;
        var index = 0;

        foreach (var item in selected)
        {
            var id = $"{groupId}:{index++}";
            var risk = item.Confidence == LeftoverConfidence.Possible ? RiskLevel.Red : RiskLevel.Yellow;

            switch (item.Kind)
            {
                case LeftoverKind.Directory:
                    var target = Describe(item);

                    // Каталог мог исчезнуть между поиском и подтверждением: пользователь
                    // мог удалить его сам, пока читал список.
                    if (target is null)
                    {
                        continue;
                    }

                    operations.Add(new DeleteFileOperation
                    {
                        Id = id,
                        GroupId = groupId,
                        DeclaredRisk = risk,
                        Consequence = DirectoryConsequence(item),
                        Target = target,

                        // Только карантин. Остаток программы — единственное, что от неё
                        // осталось: если человек ошибся с выбором, вернуть это должно быть
                        // возможно, а Корзина имеет квоту и молча стирает при переполнении.
                        Disposition = DeleteDisposition.Quarantine,
                    });

                    totalSize += target.SizeOnDiskBytes;
                    break;

                case LeftoverKind.RegistryKey:
                    var registryTarget = ParseRegistryPath(item.Path);

                    if (registryTarget is null)
                    {
                        continue;
                    }

                    operations.Add(new DeleteRegistryOperation
                    {
                        Id = id,
                        GroupId = groupId,
                        DeclaredRisk = risk,
                        Consequence = RegistryConsequence(item),
                        Target = registryTarget,
                    });

                    break;
            }
        }

        var group = new OperationGroup
        {
            GroupId = groupId,
            Title = LocalizedText.FromTranslations(new Dictionary<string, string>
            {
                ["ru"] = $"Следы программы «{app.DisplayName}»",
                ["en"] = $"Leftovers of {app.DisplayName}",
            }),
            Operations = operations,
            SizeOnDiskBytes = totalSize,
        };

        return new MutationPlan
        {
            PlanId = $"leftovers-{Guid.NewGuid():N}",
            Origin = "LeftoverPlanBuilder",
            Groups = operations.Count > 0 ? [group] : [],
        };
    }

    private static LocalizedText DirectoryConsequence(LeftoverItem item) =>
        LocalizedText.FromTranslations(new Dictionary<string, string>
        {
            ["ru"] = $"Каталог {item.Path} будет перемещён в карантин. Вернуть его можно командой отката.",
            ["en"] = $"Directory {item.Path} moves to quarantine. It can be restored with undo.",
        });

    private static LocalizedText RegistryConsequence(LeftoverItem item) =>
        LocalizedText.FromTranslations(new Dictionary<string, string>
        {
            // Про ветку реестра говорим прямо: карантина для неё нет, и это
            // должно быть сказано до нажатия, а не обнаружено после.
            ["ru"] = $"Ветка реестра {item.Path} будет удалена без возможности вернуть её из карантина.",
            ["en"] = $"Registry key {item.Path} will be deleted; quarantine does not cover registry.",
        });

    private static FileTarget? Describe(LeftoverItem item)
    {
        try
        {
            if (!Directory.Exists(item.Path))
            {
                return null;
            }

            var attributes = File.GetAttributes(item.Path);
            var traits = FileTraits.None;

            // Соединение каталогов ведёт в чужое место: удаление по такой ссылке
            // унесло бы данные, к программе не относящиеся. Признак ставится честно,
            // а отказ выносит охрана — здесь мы не решаем, а сообщаем.
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                traits |= FileTraits.ReparsePoint;
            }

            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                traits |= FileTraits.ReadOnly;
            }

            return new FileTarget(item.Path, IsDirectory: true, item.SizeOnDiskBytes, traits);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Разобрать путь ветки реестра в том виде, в каком его записывает поиск остатков.
    /// </summary>
    /// <remarks>
    /// Обратная операция к записи вида <c>HKCU\SOFTWARE\Имя</c>. Разрядность
    /// проставляется 64-разрядной не по умолчанию, а потому что поиск смотрел именно
    /// туда: потеря этого значения даёт классическую ошибку «удалили не ту ветку».
    /// </remarks>
    internal static RegistryTarget? ParseRegistryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var separator = path.IndexOf('\\');

        if (separator <= 0 || separator == path.Length - 1)
        {
            return null;
        }

        var hive = path[..separator].ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => RegistryHiveKind.CurrentUser,
            "HKLM" or "HKEY_LOCAL_MACHINE" => RegistryHiveKind.LocalMachine,
            "HKU" or "HKEY_USERS" => RegistryHiveKind.Users,
            _ => (RegistryHiveKind?)null,
        };

        if (hive is null)
        {
            return null;
        }

        var subKey = path[(separator + 1)..].Trim('\\');

        // Пустой путь означал бы весь раздел целиком. Такой цели у нас быть не может.
        return string.IsNullOrWhiteSpace(subKey)
            ? null
            : new RegistryTarget(hive.Value, subKey, ValueName: null, RegistryViewKind.Registry64);
    }
}
