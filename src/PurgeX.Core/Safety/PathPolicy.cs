namespace PurgeX.Core.Safety;

/// <summary>
/// Политика допустимости путей: что нельзя трогать никогда, а что является законной целью очистки.
/// </summary>
/// <remarks>
/// В первой версии описания проекта политика строилась на подсчёте сегментов пути
/// («минимум три сегмента ниже корня зоны»). Разбор показал, что такая проверка
/// запрещает C:\Windows\Temp и %TEMP% — то есть главную функцию продукта.
/// Поэтому подсчёт сегментов заменён явными списками.
///
/// Три уровня, в порядке убывания силы:
///   1. <see cref="AbsoluteDeny"/> — не трогаем никогда и ни при каких настройках;
///   2. <see cref="CleanRoots"/> — законные цели очистки, даже если лежат внутри запрещённой зоны;
///   3. <see cref="ProtectedZones"/> — общий запрет, снимаемый пунктом 2.
/// </remarks>
public sealed class PathPolicy
{
    private readonly List<string> _absoluteDeny = [];
    private readonly List<string> _protectedZones = [];
    private readonly List<string> _cleanRoots = [];

    /// <summary>Пути, недопустимые ни при каких условиях.</summary>
    public IReadOnlyList<string> AbsoluteDeny => _absoluteDeny;

    /// <summary>Зоны под общим запретом.</summary>
    public IReadOnlyList<string> ProtectedZones => _protectedZones;

    /// <summary>Законные корни очистки.</summary>
    public IReadOnlyList<string> CleanRoots => _cleanRoots;

    /// <summary>
    /// Собрать политику по умолчанию.
    /// </summary>
    /// <param name="windowsDirectory">Каталог Windows.</param>
    /// <param name="systemDrive">Системный диск, например «C:\».</param>
    /// <param name="ownDirectories">
    /// Каталоги самой программы: рабочий каталог, каталоги карантина, каталог распаковки
    /// вспомогательных библиотек. Их удаление ломает саму программу посреди операции.
    /// </param>
    public static PathPolicy CreateDefault(
        string windowsDirectory,
        string systemDrive,
        IEnumerable<string> ownDirectories)
    {
        var policy = new PathPolicy();

        // Никогда. Даже в продвинутом режиме, даже по прямой просьбе.
        policy._absoluteDeny.AddRange(
        [
            Path.Combine(windowsDirectory, "System32"),
            Path.Combine(windowsDirectory, "SysWOW64"),
            Path.Combine(windowsDirectory, "WinSxS"),
            Path.Combine(windowsDirectory, "Boot"),
            Path.Combine(windowsDirectory, "Fonts"),
            Path.Combine(windowsDirectory, "servicing"),
            Path.Combine(systemDrive, "Boot"),
            Path.Combine(systemDrive, "Recovery"),
            Path.Combine(systemDrive, "System Volume Information"),
            Path.Combine(systemDrive, "$Recycle.Bin"),
            Path.Combine(systemDrive, "Config.Msi"),
            Path.Combine(systemDrive, "$WinREAgent"),
        ]);

        // Собственные каталоги: программа не должна удалять сама себя.
        // Разбор отдельно отметил каталог распаковки вспомогательных библиотек —
        // он лежит во временной папке, то есть ровно там, где мы чистим.
        policy._absoluteDeny.AddRange(ownDirectories);

        // Общий запрет: сюда без явного разрешения не ходим.
        policy._protectedZones.AddRange(
        [
            windowsDirectory,
            Path.Combine(systemDrive, "Program Files"),
            Path.Combine(systemDrive, "Program Files (x86)"),
            Path.Combine(systemDrive, "ProgramData"),
            Path.Combine(systemDrive, "Users"),
        ]);

        // Законные цели очистки внутри запрещённых зон.
        policy._cleanRoots.AddRange(
        [
            Path.Combine(windowsDirectory, "Temp"),
            Path.Combine(windowsDirectory, "Prefetch"),
            Path.Combine(windowsDirectory, "SoftwareDistribution", "Download"),
            Path.Combine(windowsDirectory, "Logs"),
        ]);

        return policy;
    }

    /// <summary>Добавить законный корень очистки (например, кэш браузера в профиле пользователя).</summary>
    public void AddCleanRoot(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            _cleanRoots.Add(Normalize(path));
        }
    }

    /// <summary>Добавить путь, недопустимый ни при каких условиях.</summary>
    public void AddAbsoluteDeny(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            _absoluteDeny.Add(Normalize(path));
        }
    }

    /// <summary>Проверить путь.</summary>
    public PathDecision Evaluate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return PathDecision.Denied("путь пуст");
        }

        string normalized;
        try
        {
            normalized = Normalize(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return PathDecision.Denied("путь не удалось разобрать");
        }

        // Корень тома целиком — цель, которой не может быть законной причины.
        if (IsVolumeRoot(normalized))
        {
            return PathDecision.Denied("нельзя работать с корнем диска целиком");
        }

        foreach (var denied in _absoluteDeny)
        {
            if (IsSameOrInside(normalized, Normalize(denied)))
            {
                return PathDecision.Denied($"защищённый системный путь: {denied}");
            }
        }

        // Законная цель имеет приоритет над общим запретом зоны,
        // но никогда — над безусловным запретом выше.
        foreach (var root in _cleanRoots)
        {
            if (IsSameOrInside(normalized, Normalize(root)))
            {
                return PathDecision.Allowed();
            }
        }

        foreach (var zone in _protectedZones)
        {
            var normalizedZone = Normalize(zone);

            // Сама зона целиком — недопустимая цель.
            if (string.Equals(normalized, normalizedZone, StringComparison.OrdinalIgnoreCase))
            {
                return PathDecision.Denied($"нельзя работать с каталогом целиком: {zone}");
            }

            if (IsSameOrInside(normalized, normalizedZone))
            {
                // Внутрь защищённой зоны пускаем, но помечаем — риск повышается охраной.
                return PathDecision.AllowedWithCaution($"внутри защищённой зоны: {zone}");
            }
        }

        return PathDecision.Allowed();
    }

    /// <summary>Приведение пути к сравнимому виду.</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// Лежит ли путь внутри другого или совпадает с ним.
    /// </summary>
    /// <remarks>
    /// Сравнение идёт посегментно, а не по префиксу строки: иначе «C:\Program Files Custom»
    /// считался бы находящимся внутри «C:\Program Files».
    /// </remarks>
    public static bool IsSameOrInside(string path, string container)
    {
        if (string.Equals(path, container, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var withSeparator = container.EndsWith(Path.DirectorySeparatorChar)
            ? container
            : container + Path.DirectorySeparatorChar;

        return path.StartsWith(withSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVolumeRoot(string normalized)
    {
        // После нормализации корень выглядит как «C:» — завершающий разделитель уже убран.
        return normalized.Length <= 3 && normalized.Contains(':');
    }
}

/// <summary>Решение по пути.</summary>
/// <param name="IsAllowed">Работать с путём можно.</param>
/// <param name="RequiresCaution">Работать можно, но уровень риска должен быть повышен.</param>
/// <param name="Reason">Обоснование для показа пользователю и записи в журнал.</param>
public readonly record struct PathDecision(bool IsAllowed, bool RequiresCaution, string? Reason)
{
    public static PathDecision Allowed() => new(true, false, null);
    public static PathDecision AllowedWithCaution(string reason) => new(true, true, reason);
    public static PathDecision Denied(string reason) => new(false, false, reason);
}
