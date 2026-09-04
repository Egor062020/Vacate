using Microsoft.Win32;
using Vacate.Abstractions.Model;

namespace Vacate.Platform.Windows.Registry;

/// <summary>
/// Список установленных программ из реестра.
/// </summary>
/// <remarks>
/// Фильтрация здесь важнее самого перечисления. Без неё в список попадают обновления,
/// пакеты драйверов и служебные компоненты, которые панель управления Windows намеренно
/// скрывает, — и раздел «давно не запускалось» предложит пользователю удалить пакет драйвера.
///
/// Правила отсева взяты из того, как это делает сама Windows:
///   - запись без названия — служебная;
///   - признак системного компонента — скрывается;
///   - запись, помеченная как принадлежащая другой записи, — это обновление, а не программа;
///   - системные обновления с характерными признаками не показываются.
/// </remarks>
public sealed class InstalledAppsScanner
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Собрать список установленных программ.</summary>
    public IReadOnlyList<InstalledApp> Scan(CancellationToken ct = default)
    {
        var found = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);

        // Порядок обхода задаёт приоритет: запись для конкретного пользователя
        // перекрывает одноимённую общесистемную, потому что именно она относится к нему.
        ReadInto(found, RegistryHive.LocalMachine, RegistryView.Registry64, InstallScope.Machine, is32Bit: false, ct);
        ReadInto(found, RegistryHive.LocalMachine, RegistryView.Registry32, InstallScope.Machine, is32Bit: true, ct);
        ReadInto(found, RegistryHive.CurrentUser, RegistryView.Registry64, InstallScope.User, is32Bit: false, ct);
        ReadInto(found, RegistryHive.CurrentUser, RegistryView.Registry32, InstallScope.User, is32Bit: true, ct);

        return found.Values
            .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void ReadInto(
        Dictionary<string, InstalledApp> destination,
        RegistryHive hive,
        RegistryView view,
        InstallScope scope,
        bool is32Bit,
        CancellationToken ct)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallPath);

            if (uninstall is null)
            {
                return;
            }

            foreach (var subKeyName in uninstall.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();

                using var key = uninstall.OpenSubKey(subKeyName);

                if (key is null)
                {
                    continue;
                }

                var app = TryRead(key, subKeyName, scope, is32Bit);

                if (app is not null)
                {
                    destination[app.DisplayName] = app;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // Часть веток может быть недоступна. Это не повод показывать пустой список.
        }
    }

    private static InstalledApp? TryRead(RegistryKey key, string keyName, InstallScope scope, bool is32Bit)
    {
        var displayName = key.GetValue("DisplayName") as string;

        // Запись без названия — служебная, показывать её пользователю нечем.
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        // Признак системного компонента: панель управления такие прячет, и не зря.
        if (key.GetValue("SystemComponent") is int component && component == 1)
        {
            return null;
        }

        // Запись принадлежит другой записи — это обновление, а не самостоятельная программа.
        if (key.GetValue("ParentKeyName") is string parent && !string.IsNullOrWhiteSpace(parent))
        {
            return null;
        }

        if (key.GetValue("ReleaseType") is string releaseType
            && (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase)
                || releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var uninstallCommand = key.GetValue("UninstallString") as string;
        var quiet = key.GetValue("QuietUninstallString") as string;

        // Программы, установленные через системный установщик, часто не пишут команду удаления,
        // но её можно собрать из кода продукта — именем ключа как раз он и является.
        if (string.IsNullOrWhiteSpace(uninstallCommand)
            && key.GetValue("WindowsInstaller") is int msi && msi == 1
            && keyName.StartsWith('{'))
        {
            uninstallCommand = $"msiexec.exe /x {keyName}";
            quiet ??= $"msiexec.exe /x {keyName} /qn";
        }

        return new InstalledApp(
            Id: keyName,
            DisplayName: displayName!,
            Version: key.GetValue("DisplayVersion") as string,
            Publisher: key.GetValue("Publisher") as string,
            InstallLocation: NormalizeLocation(key.GetValue("InstallLocation") as string),
            UninstallCommand: uninstallCommand,
            QuietUninstallCommand: quiet,
            InstallDate: ParseInstallDate(key.GetValue("InstallDate") as string),
            // Значение хранится в килобайтах.
            EstimatedSizeBytes: key.GetValue("EstimatedSize") is int size ? (long)size * 1024 : 0,
            Scope: scope,
            Is32BitOnWin64: is32Bit,
            IconPath: key.GetValue("DisplayIcon") as string);
    }

    private static string? NormalizeLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        return location.Trim().Trim('"').TrimEnd('\\');
    }

    private static DateOnly? ParseInstallDate(string? raw)
    {
        // Формат «ггггммдд», но пишут его не все и не всегда правильно.
        if (string.IsNullOrWhiteSpace(raw) || raw.Length != 8)
        {
            return null;
        }

        return DateOnly.TryParseExact(raw, "yyyyMMdd", out var date) ? date : null;
    }
}
