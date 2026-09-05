using System.Diagnostics;
using Microsoft.Win32;
using Vacate.Abstractions.Model;

// Наш тип значения реестра и системный называются одинаково.
// Псевдоним убирает неоднозначность и делает явным, о каком из двух идёт речь.
using Win32ValueKind = Microsoft.Win32.RegistryValueKind;

namespace Vacate.Platform.Windows.Registry;

/// <summary>
/// Включение и отключение автозапуска.
/// </summary>
/// <remarks>
/// Три источника — три разных механизма, и ни один нельзя заменить простым удалением:
///
///   1. Ключи Run: запись НЕ удаляется, а помечается в отдельной ветке — тем же способом,
///      которым пользуется диспетчер задач. Удаление было бы необратимым, а человек,
///      отключивший автозапуск, обычно хочет иметь возможность вернуть его.
///   2. Папка автозагрузки: ярлык переименовывается, а не стирается. Обратно —
///      тем же движением.
///   3. Службы: только через штатный диспетчер служб. Правка поля Start прямо в реестре
///      обходит его собственные проверки и приводит к состояниям, из которых система
///      не всегда выходит.
///
/// Отключённая служба переводится в режим «вручную», а не «отключена». Разница
/// существенная: программа, которой служба действительно нужна, поднимет её сама,
/// и человек не получит неработающую программу вместо ускоренной загрузки.
/// </remarks>
public sealed class StartupToggle
{
    private const string RunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedRunPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>Расширение, которым система помечает отключённый ярлык автозагрузки.</summary>
    private const string DisabledSuffix = ".disabled";

    /// <summary>Нужны ли для переключения этой записи права администратора.</summary>
    public static bool RequiresElevation(StartupEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        return entry.Source switch
        {
            // Общесистемные ветки и общая папка автозагрузки пользователю недоступны.
            StartupSource.Service => true,
            _ => entry.Scope == InstallScope.Machine,
        };
    }

    /// <summary>Переключить запись.</summary>
    public ToggleOutcome Set(StartupEntry entry, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (entry.Control == StartupControl.ViewOnly)
        {
            // Запрет известен заранее и показан неактивной кнопкой. Досюда
            // дойти можно только из консоли, и ответ должен быть тем же.
            return ToggleOutcome.Refused(entry.Note ?? "Эту запись переключать нельзя");
        }

        return entry.Source switch
        {
            StartupSource.RunKey => SetRunKey(entry, enabled),
            StartupSource.StartupFolder => SetStartupFolder(entry, enabled),
            StartupSource.Service => SetService(entry, enabled),
            _ => ToggleOutcome.Refused("Этот вид автозапуска переключать нельзя"),
        };
    }

    /// <summary>
    /// Пометить запись Run включённой или отключённой.
    /// </summary>
    /// <remarks>
    /// Формат значения недокументирован, но неизменен со времён Windows 8: двенадцать
    /// байт, где первый — состояние (чётный включено, нечётный отключено), а следом
    /// время отключения. Диспетчер задач пишет ровно это.
    /// </remarks>
    private static ToggleOutcome SetRunKey(StartupEntry entry, bool enabled)
    {
        var (hive, view, valueName) = ParseRunId(entry.Id);

        if (valueName is null)
        {
            return ToggleOutcome.Refused("Не удалось разобрать запись автозапуска");
        }

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var run = baseKey.OpenSubKey(RunPath);

            if (run?.GetValue(valueName) is null)
            {
                return ToggleOutcome.Refused("Запись исчезла: её мог убрать сам установщик");
            }

            using var approved = baseKey.CreateSubKey(ApprovedRunPath, writable: true);

            if (approved is null)
            {
                return ToggleOutcome.Refused("Не удалось открыть ветку состояний автозапуска");
            }

            var state = new byte[12];
            state[0] = enabled ? (byte)0x02 : (byte)0x03;

            if (!enabled)
            {
                // Время отключения: диспетчер задач показывает его в своём списке.
                BitConverter.GetBytes(DateTime.UtcNow.ToFileTimeUtc()).CopyTo(state, 4);
            }

            approved.SetValue(valueName, state, Win32ValueKind.Binary);

            return ToggleOutcome.Done(enabled);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            return ToggleOutcome.Refused("Не хватает прав: эта запись общая для всех пользователей");
        }
    }

    /// <summary>Переименовать ярлык в папке автозагрузки.</summary>
    private static ToggleOutcome SetStartupFolder(StartupEntry entry, bool enabled)
    {
        var current = entry.Id;

        if (!File.Exists(current))
        {
            return ToggleOutcome.Refused("Файл исчез из папки автозагрузки");
        }

        var target = enabled
            ? current.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)
                ? current[..^DisabledSuffix.Length]
                : current
            : current + DisabledSuffix;

        if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
        {
            // Уже в нужном состоянии. Это не ошибка.
            return ToggleOutcome.Done(enabled);
        }

        try
        {
            if (File.Exists(target))
            {
                return ToggleOutcome.Refused($"Рядом уже лежит файл {Path.GetFileName(target)}");
            }

            File.Move(current, target);

            return ToggleOutcome.Done(enabled, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToggleOutcome.Refused($"Не удалось переименовать файл: {ex.Message}");
        }
    }

    /// <summary>
    /// Переключить автозапуск службы через штатный диспетчер служб.
    /// </summary>
    /// <remarks>
    /// Отключение переводит службу в режим «вручную», а не «отключена». Полное отключение
    /// ломает программы, которые поднимают свою службу по требованию, и связь поломки
    /// с этим действием человек уже не восстановит.
    /// </remarks>
    private static ToggleOutcome SetService(StartupEntry entry, bool enabled)
    {
        var serviceName = entry.Id.StartsWith("service:", StringComparison.Ordinal)
            ? entry.Id["service:".Length..]
            : null;

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return ToggleOutcome.Refused("Не удалось определить имя службы");
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System), "sc.exe"),

                // Пробел после «start=» обязателен: без него диспетчер служб
                // отвечает справкой по использованию, а не выполняет команду.
                Arguments = $"config \"{serviceName}\" start= {(enabled ? "auto" : "demand")}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return ToggleOutcome.Refused("Не удалось обратиться к диспетчеру служб");
            }

            process.WaitForExit();

            if (process.ExitCode == 0)
            {
                return ToggleOutcome.Done(enabled);
            }

            const int AccessDenied = 5;

            return ToggleOutcome.Refused(process.ExitCode == AccessDenied
                ? "Не хватает прав администратора для изменения службы"
                : $"Диспетчер служб отказал (код {process.ExitCode})");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return ToggleOutcome.Refused(ex.Message);
        }
    }

    /// <summary>Разобрать идентификатор записи ключа Run.</summary>
    internal static (RegistryHive Hive, RegistryView View, string? ValueName) ParseRunId(string id)
    {
        var separator = id.IndexOf(':');

        if (separator <= 0 || separator == id.Length - 1)
        {
            return (RegistryHive.CurrentUser, RegistryView.Registry64, null);
        }

        var prefix = id[..separator];
        var name = id[(separator + 1)..];

        return prefix switch
        {
            "HKLM" => (RegistryHive.LocalMachine, RegistryView.Registry64, name),

            // Разрядность — часть адреса: 32-разрядные программы прописываются
            // в собственное представление того же ключа.
            "HKLM32" => (RegistryHive.LocalMachine, RegistryView.Registry32, name),
            "HKCU" => (RegistryHive.CurrentUser, RegistryView.Registry64, name),
            _ => (RegistryHive.CurrentUser, RegistryView.Registry64, null),
        };
    }
}

/// <param name="Success">Состояние изменено.</param>
/// <param name="Enabled">Новое состояние.</param>
/// <param name="Message">Причина отказа для показа человеку.</param>
/// <param name="NewId">
/// Новый идентификатор записи, если он изменился. Для папки автозагрузки это так:
/// отключение переименовывает файл, и старый путь перестаёт существовать.
/// </param>
public sealed record ToggleOutcome(bool Success, bool Enabled, string? Message = null, string? NewId = null)
{
    public static ToggleOutcome Done(bool enabled, string? newId = null) => new(true, enabled, null, newId);

    public static ToggleOutcome Refused(string message) => new(false, false, message);
}
