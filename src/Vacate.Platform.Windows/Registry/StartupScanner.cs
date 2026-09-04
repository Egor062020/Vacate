using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Vacate.Abstractions.Model;

namespace Vacate.Platform.Windows.Registry;

/// <summary>
/// Читает всё, что стартует вместе с Windows.
/// </summary>
/// <remarks>
/// Диспетчер задач показывает только часть картины — ключи Run и папки автозагрузки.
/// Львиная доля автозапуска у обычного пользователя прячется в задачах планировщика
/// и службах: обновлятели, агенты синхронизации, вспомогательные процессы игровых
/// лаунчеров. Поэтому источников больше.
///
/// Службы читаются из реестра, но ПЕРЕКЛЮЧАТЬСЯ будут через диспетчер служб:
/// правка поля Start в обход диспетчера обходит его собственные проверки и приводит
/// к состояниям, из которых система не всегда выходит.
/// </remarks>
public sealed class StartupScanner
{
    private const string RunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ServicesPath = @"SYSTEM\CurrentControlSet\Services";

    /// <summary>
    /// Службы, которые нельзя отключать ни при каких условиях.
    /// </summary>
    /// <remarks>
    /// Здесь и защита Windows, и то, без чего система не загрузится или потеряет сеть.
    /// Показываем их в списке — пользователь имеет право видеть картину целиком, —
    /// но переключатель неактивен с пояснением.
    /// </remarks>
    private static readonly HashSet<string> ProtectedServices = new(StringComparer.OrdinalIgnoreCase)
    {
        // Защита системы. Отключение антивирусной защиты вне наших границ.
        "WinDefend", "WdNisSvc", "SecurityHealthService", "Sense", "wscsvc", "mpssvc",
        // Основа работы системы.
        "RpcSs", "RpcEptMapper", "DcomLaunch", "PlugPlay", "Power", "ProfSvc",
        "CryptSvc", "EventLog", "Schedule", "SamSs", "LSM", "UserManager",
        // Сеть и вход в систему.
        "Dhcp", "Dnscache", "NlaSvc", "netprofm", "nsi", "LanmanWorkstation",
        // Установка и обновление: их поломка делает систему неремонтопригодной.
        "msiserver", "TrustedInstaller", "wuauserv", "BITS",
        // Ввод и вывод.
        "AudioSrv", "AudioEndpointBuilder", "Themes", "ShellHWDetection",
    };

    /// <summary>Собрать все записи автозапуска.</summary>
    public IReadOnlyList<StartupEntry> Scan(CancellationToken ct = default)
    {
        var entries = new List<StartupEntry>();

        ReadRunKeys(entries, ct);
        ReadStartupFolders(entries, ct);
        ReadServices(entries, ct);

        return entries
            .OrderBy(e => e.Source)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void ReadRunKeys(List<StartupEntry> entries, CancellationToken ct)
    {
        var sources = new (RegistryHive Hive, RegistryView View, InstallScope Scope, string Label)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, InstallScope.Machine, "HKLM"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, InstallScope.Machine, "HKLM32"),
            (RegistryHive.CurrentUser, RegistryView.Registry64, InstallScope.User, "HKCU"),
        };

        foreach (var (hive, view, scope, label) in sources)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var run = baseKey.OpenSubKey(RunPath);

                if (run is null)
                {
                    continue;
                }

                var disabled = ReadApprovedStates(baseKey);

                foreach (var name in run.GetValueNames())
                {
                    if (run.GetValue(name) is not string command || string.IsNullOrWhiteSpace(command))
                    {
                        continue;
                    }

                    entries.Add(new StartupEntry(
                        Id: $"{label}:{name}",
                        Name: name,
                        Command: command,
                        ImagePath: ExtractImagePath(command),
                        Source: StartupSource.RunKey,
                        Scope: scope,
                        IsEnabled: !disabled.Contains(name),
                        Control: StartupControl.Toggleable));
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                // Часть веток недоступна без повышенных прав — покажем то, что смогли прочитать.
            }
        }
    }

    /// <summary>
    /// Какие записи отключены через механизм, которым пользуется диспетчер задач.
    /// </summary>
    /// <remarks>
    /// Отключение там не удаляет запись из Run, а помечает её в отдельной ветке.
    /// Первый байт значения: чётный — включено, нечётный — отключено. Формат
    /// недокументирован, но неизменен со времён Windows 8; риск принят осознанно,
    /// потому что альтернатива — удалять записи, а это уже необратимо.
    /// </remarks>
    private static HashSet<string> ReadApprovedStates(RegistryKey baseKey)
    {
        var disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var approved = baseKey.OpenSubKey(ApprovedRunPath);

            if (approved is null)
            {
                return disabled;
            }

            foreach (var name in approved.GetValueNames())
            {
                if (approved.GetValue(name) is byte[] { Length: > 0 } state && (state[0] & 1) == 1)
                {
                    disabled.Add(name);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // Нет доступа — считаем всё включённым, это безопаснее для показа.
        }

        return disabled;
    }

    private void ReadStartupFolders(List<StartupEntry> entries, CancellationToken ct)
    {
        var folders = new (string Path, InstallScope Scope)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.Startup), InstallScope.User),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), InstallScope.Machine),
        };

        foreach (var (folder, scope) in folders)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.GetFiles(folder))
                {
                    // В папке автозагрузки лежат не только запускаемые объекты: Windows
                    // хранит там служебный файл с настройками отображения папки.
                    // Проверка на живой машине показала его в списке автозапуска
                    // под именем «desktop» — как будто это программа.
                    if (!IsLaunchable(file))
                    {
                        continue;
                    }

                    var name = Path.GetFileNameWithoutExtension(file);

                    // Отключённые ярлыки система помечает расширением .disabled
                    var isDisabled = file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);

                    entries.Add(new StartupEntry(
                        Id: file,
                        Name: name,
                        Command: file,
                        ImagePath: file,
                        Source: StartupSource.StartupFolder,
                        Scope: scope,
                        IsEnabled: !isDisabled,
                        Control: StartupControl.Toggleable));
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Папка может быть недоступна.
            }
        }
    }

    private void ReadServices(List<StartupEntry> entries, CancellationToken ct)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var services = baseKey.OpenSubKey(ServicesPath);

            if (services is null)
            {
                return;
            }

            foreach (var serviceName in services.GetSubKeyNames())
            {
                ct.ThrowIfCancellationRequested();

                using var key = services.OpenSubKey(serviceName);

                if (key is null)
                {
                    continue;
                }

                if (key.GetValue("Type") is not int type)
                {
                    continue;
                }

                // Драйверы ядра и файловых систем — не автозагрузка программ.
                // Показывать их вперемешку с обновлятелями значит запутать пользователя
                // и подтолкнуть его отключить то, без чего не работает железо.
                const int ServiceKernelDriver = 0x1;
                const int ServiceFileSystemDriver = 0x2;

                if (type is ServiceKernelDriver or ServiceFileSystemDriver)
                {
                    continue;
                }

                if (key.GetValue("Start") is not int start)
                {
                    continue;
                }

                // 0 - при загрузке, 1 - системная, 2 - автоматически, 3 - вручную, 4 - отключена.
                // В автозагрузку попадают только первые три.
                const int StartDisabled = 4;
                const int StartManual = 3;

                if (start > StartManual)
                {
                    // Отключённые тоже показываем: пользователь должен видеть,
                    // что он отключил раньше, и иметь возможность вернуть.
                }
                else if (start == StartManual)
                {
                    // Запускается по требованию, а не при старте системы.
                    continue;
                }

                var display = ResolveDisplayName(key.GetValue("DisplayName") as string, serviceName);
                var imagePath = key.GetValue("ImagePath") as string ?? string.Empty;
                var isProtected = ProtectedServices.Contains(serviceName);

                entries.Add(new StartupEntry(
                    Id: $"service:{serviceName}",
                    Name: display,
                    Command: imagePath,
                    ImagePath: ExtractImagePath(imagePath),
                    Source: StartupSource.Service,
                    Scope: InstallScope.Machine,
                    IsEnabled: start != StartDisabled,
                    Control: isProtected ? StartupControl.ViewOnly : StartupControl.Toggleable,
                    Note: isProtected
                        ? "Критичная системная служба: отключение может сделать систему неработоспособной"
                        : null));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // Без повышенных прав список служб может быть недоступен целиком.
        }
    }

    /// <summary>
    /// Привести название службы к читаемому виду.
    /// </summary>
    /// <remarks>
    /// Windows хранит названия системных служб не строкой, а ссылкой на ресурс внутри
    /// библиотеки: «@%SystemRoot%\system32\audiosrv.dll,-210». Показывать это пользователю
    /// нельзя — проверка на живой машине дала именно такой список, нечитаемый целиком.
    /// Ссылка разрешается системным вызовом; если он не сработал, показываем короткое
    /// служебное имя, а не техническую строку.
    /// </remarks>
    internal static string ResolveDisplayName(string? displayName, string serviceName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return serviceName;
        }

        if (!displayName.StartsWith('@'))
        {
            return displayName;
        }

        try
        {
            var buffer = new StringBuilder(1024);

            if (SHLoadIndirectString(displayName, buffer, buffer.Capacity, IntPtr.Zero) == 0)
            {
                var resolved = buffer.ToString();

                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    return resolved;
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Останемся со служебным именем — оно хотя бы читаемое.
        }

        return serviceName;
    }

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern int SHLoadIndirectString(string pszSource, StringBuilder pszOutBuf, int cchOutBuf, IntPtr ppvReserved);

    /// <summary>
    /// Может ли файл из папки автозагрузки быть запущен.
    /// </summary>
    /// <remarks>
    /// Отсекает служебные файлы Windows, которые лежат в тех же папках,
    /// но программами не являются. Список расширений задан явно: перечислить
    /// запускаемое короче и надёжнее, чем угадывать служебное.
    /// </remarks>
    internal static bool IsLaunchable(string path)
    {
        // Отключённые записи сохраняют исходное расширение перед добавленным суффиксом.
        var effective = path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? path[..^".disabled".Length]
            : path;

        var extension = Path.GetExtension(effective).ToLowerInvariant();

        return extension is ".lnk" or ".exe" or ".bat" or ".cmd" or ".vbs" or ".js" or ".ps1" or ".url" or ".com";
    }

    /// <summary>Выделить путь к программе из команды запуска.</summary>
    internal static string? ExtractImagePath(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var trimmed = command.Trim();

        if (trimmed.StartsWith('"'))
        {
            var closing = trimmed.IndexOf('"', 1);

            if (closing > 0)
            {
                return trimmed[1..closing];
            }
        }

        // Службы записываются с системным префиксом пути.
        if (trimmed.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            trimmed = trimmed[4..];
        }

        var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);

        return exeIndex > 0 ? trimmed[..(exeIndex + 4)] : null;
    }

    /// <summary>Является ли служба защищённой от отключения.</summary>
    public static bool IsProtectedService(string serviceName) => ProtectedServices.Contains(serviceName);
}
