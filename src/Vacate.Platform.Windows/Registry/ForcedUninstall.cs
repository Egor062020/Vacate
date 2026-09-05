using Microsoft.Win32;
using Vacate.Abstractions.Model;

namespace Vacate.Platform.Windows.Registry;

/// <summary>
/// Принудительное удаление: когда штатного деинсталлятора уже нет.
/// </summary>
/// <remarks>
/// Случай распространённый и раздражающий: программу удалили вручную или её деинсталлятор
/// сломался, а запись в списке установленного осталась. Штатно удалить нечем — команда
/// ведёт на несуществующий файл, — и программа висит в списке навсегда.
///
/// Опасность здесь другая, чем при обычном удалении. Там мы шли за деинсталлятором,
/// который знал, что удалять. Здесь знать некому, и решение принимается по косвенным
/// признакам, поэтому:
///
///   1. Принудительное удаление предлагается ТОЛЬКО когда деинсталлятора действительно
///      нет. Пока он есть, идём через него: он знает про свою программу больше нас.
///   2. Запись из списка убирается ПОСЛЕДНЕЙ. Уберите её первой — и, если удаление
///      файлов сорвётся, программа исчезнет из списка, оставшись на диске: следов
///      не найти, потому что искать больше не от чего.
/// </remarks>
public sealed class ForcedUninstall
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Имеет ли смысл предлагать принудительное удаление.</summary>
    /// <remarks>
    /// Пока штатный деинсталлятор на месте, предлагать наш способ нельзя: он заведомо
    /// хуже. Мы удалим файлы, а программа могла держать службу, драйвер или задачу,
    /// про которые знает только её собственный деинсталлятор.
    /// </remarks>
    public static bool IsApplicable(InstalledApp app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (string.IsNullOrWhiteSpace(app.UninstallCommand))
        {
            // Команды нет вовсе — штатно удалять нечем.
            return true;
        }

        var (executable, _) = UninstallRunner.SplitCommand(app.UninstallCommand);

        // Команда есть, а файла нет: запись пережила саму программу.
        return Path.IsPathRooted(executable) && !File.Exists(executable);
    }

    /// <summary>
    /// Убрать запись из списка установленного.
    /// </summary>
    /// <remarks>
    /// Выполняется последней, после удаления файлов. Пока запись на месте, человек
    /// видит программу в списке и может попробовать ещё раз; убрав её раньше времени,
    /// мы лишили бы его этой возможности.
    /// </remarks>
    public ForcedUninstallOutcome RemoveRegistration(InstalledApp app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Разрядность обязательна: 32-разрядные программы прописываются
        // в собственное представление того же пути.
        var view = app.Is32BitOnWin64 ? RegistryView.Registry32 : RegistryView.Registry64;

        var hives = app.Scope == InstallScope.User
            ? [RegistryHive.CurrentUser]
            : new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser };

        foreach (var hive in hives)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(UninstallPath, writable: true);

                if (uninstall?.OpenSubKey(app.Id) is null)
                {
                    continue;
                }

                uninstall.DeleteSubKeyTree(app.Id, throwOnMissingSubKey: false);

                return new ForcedUninstallOutcome(true, "Запись убрана из списка установленных программ");
            }
            catch (UnauthorizedAccessException)
            {
                return new ForcedUninstallOutcome(false,
                    "Не хватает прав: запись общая для всех пользователей компьютера");
            }
            catch (System.Security.SecurityException)
            {
                return new ForcedUninstallOutcome(false, "Доступ к записи закрыт");
            }
        }

        return new ForcedUninstallOutcome(false, "Запись в списке установленного не найдена");
    }
}

/// <param name="Success">Запись убрана.</param>
/// <param name="Message">Пояснение человеческим языком.</param>
public sealed record ForcedUninstallOutcome(bool Success, string Message);
