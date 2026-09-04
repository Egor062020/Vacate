using System.Security.Principal;
using Microsoft.Win32.TaskScheduler;

namespace Vacate.Platform.Windows.Registry;

/// <summary>
/// Управляет задачей автоматической очистки в планировщике Windows.
/// </summary>
/// <remarks>
/// Задача регистрируется СТРОГО на конкретного пользователя, с входом в его сеансе.
/// Соблазн зарегистрировать её от системной учётной записи выглядит удобным — работает
/// без пароля и всегда, — но тогда «свой временный каталог» и «свой профиль» окажутся
/// каталогами системной учётной записи. Пользователь получит еженедельный отчёт
/// «очищено, освобождено 0 байт» и потеряет доверие к продукту на первом же
/// автоматическом запуске.
///
/// Автоматически выполняются только безопасные категории: временные файлы и кэши.
/// Реестр и удаление программ в автоматическом режиме не трогаются никогда —
/// действие без человека за экраном не должно быть необратимым.
/// </remarks>
public sealed class ScheduleManager
{
    /// <summary>Имя папки задач продукта в планировщике.</summary>
    public const string TaskFolder = "Vacate";

    /// <summary>Имя задачи.</summary>
    public const string TaskName = "Автоматическая очистка";

    private static string FullTaskPath => $@"\{TaskFolder}\{TaskName}";

    /// <summary>Текущее состояние расписания.</summary>
    public ScheduleState GetState()
    {
        try
        {
            using var service = new TaskService();
            using var task = service.GetTask(FullTaskPath);

            if (task is null)
            {
                return new ScheduleState(false, null, null);
            }

            var trigger = task.Definition.Triggers.FirstOrDefault();

            return new ScheduleState(
                task.Enabled,
                DescribeTrigger(trigger),
                task.NextRunTime == DateTime.MinValue ? null : task.NextRunTime);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Планировщик может быть недоступен: служба отключена или нет прав.
            return new ScheduleState(false, null, null);
        }
    }

    /// <summary>Включить автоматическую очистку.</summary>
    /// <param name="executablePath">Путь к программе.</param>
    /// <param name="frequency">Как часто запускать.</param>
    /// <param name="atLogon">Дополнительно запускать при входе в систему.</param>
    public ScheduleResult Enable(string executablePath, ScheduleFrequency frequency, bool atLogon)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        if (!File.Exists(executablePath))
        {
            return new ScheduleResult(false, "Файл программы не найден. Расписание не создано");
        }

        // Программа, запущенная со съёмного носителя, при следующей загрузке может
        // оказаться по другому пути или вовсе отсутствовать. Задача, ссылающаяся
        // в пустоту, будет молча падать каждую неделю.
        if (IsOnRemovableDrive(executablePath))
        {
            return new ScheduleResult(false,
                "Программа запущена со съёмного носителя. Расписание работать не будет: "
                + "при следующей загрузке буква диска может измениться");
        }

        try
        {
            using var service = new TaskService();
            var definition = service.NewTask();

            definition.RegistrationInfo.Description =
                "Автоматическая очистка временных файлов и кэшей. Реестр и программы не затрагиваются.";
            definition.RegistrationInfo.Author = "Vacate";

            // Запуск в сеансе конкретного пользователя, а не системной учётной записи.
            definition.Principal.LogonType = TaskLogonType.InteractiveToken;
            definition.Principal.UserId = WindowsIdentity.GetCurrent().Name;
            definition.Principal.RunLevel = TaskRunLevel.LUA;

            definition.Settings.StartWhenAvailable = true;
            definition.Settings.DisallowStartIfOnBatteries = true;
            definition.Settings.StopIfGoingOnBatteries = true;
            definition.Settings.ExecutionTimeLimit = TimeSpan.FromHours(1);

            switch (frequency)
            {
                case ScheduleFrequency.Daily:
                    definition.Triggers.Add(new DailyTrigger { StartBoundary = DateTime.Today.AddHours(13) });
                    break;

                case ScheduleFrequency.Monthly:
                    definition.Triggers.Add(new MonthlyTrigger { StartBoundary = DateTime.Today.AddHours(13) });
                    break;

                default:
                    definition.Triggers.Add(new WeeklyTrigger
                    {
                        StartBoundary = DateTime.Today.AddHours(13),
                        DaysOfWeek = DaysOfTheWeek.Sunday,
                    });
                    break;
            }

            if (atLogon)
            {
                // С задержкой: сразу после входа система и так занята,
                // и очистка в этот момент только замедлит запуск.
                definition.Triggers.Add(new LogonTrigger { Delay = TimeSpan.FromMinutes(3) });
            }

            definition.Actions.Add(new ExecAction(executablePath, "--quiet-clean"));

            service.RootFolder
                .CreateFolder(TaskFolder, exceptionOnExists: false)
                .RegisterTaskDefinition(TaskName, definition);

            return new ScheduleResult(true, "Автоматическая очистка включена");
        }
        catch (UnauthorizedAccessException)
        {
            return new ScheduleResult(false, "Недостаточно прав для создания задачи в планировщике");
        }
        catch (Exception ex)
        {
            return new ScheduleResult(false, $"Не удалось создать задачу: {ex.Message}");
        }
    }

    /// <summary>Выключить автоматическую очистку.</summary>
    public ScheduleResult Disable()
    {
        try
        {
            using var service = new TaskService();
            var folder = service.GetFolder($@"\{TaskFolder}");

            if (folder is null)
            {
                return new ScheduleResult(true, "Расписание и так не настроено");
            }

            folder.DeleteTask(TaskName, exceptionOnNotExists: false);

            return new ScheduleResult(true, "Автоматическая очистка выключена");
        }
        catch (UnauthorizedAccessException)
        {
            return new ScheduleResult(false, "Недостаточно прав для изменения задачи");
        }
        catch (Exception ex)
        {
            return new ScheduleResult(false, $"Не удалось удалить задачу: {ex.Message}");
        }
    }

    private static bool IsOnRemovableDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));

            if (string.IsNullOrEmpty(root))
            {
                return false;
            }

            var drive = new DriveInfo(root);

            return drive.DriveType is DriveType.Removable or DriveType.Network;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return false;
        }
    }

    private static string DescribeTrigger(Trigger? trigger) => trigger switch
    {
        DailyTrigger => "каждый день",
        WeeklyTrigger => "раз в неделю",
        MonthlyTrigger => "раз в месяц",
        LogonTrigger => "при входе в систему",
        null => "не задано",
        _ => "по расписанию",
    };
}

/// <param name="Enabled">Задача существует и включена.</param>
/// <param name="Frequency">Как часто запускается.</param>
/// <param name="NextRun">Когда сработает в следующий раз.</param>
public sealed record ScheduleState(bool Enabled, string? Frequency, DateTime? NextRun);

/// <param name="Success">Удалось ли изменить расписание.</param>
/// <param name="Message">Пояснение человеческим языком.</param>
public sealed record ScheduleResult(bool Success, string Message);

/// <summary>Как часто выполнять автоматическую очистку.</summary>
public enum ScheduleFrequency
{
    Daily,
    Weekly,
    Monthly,
}
