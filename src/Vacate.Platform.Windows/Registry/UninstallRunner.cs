using System.Diagnostics;
using System.Runtime.InteropServices;
using Vacate.Abstractions.Model;

namespace Vacate.Platform.Windows.Registry;

/// <summary>
/// Запуск штатного деинсталлятора программы.
/// </summary>
/// <remarks>
/// Главная сложность не в запуске, а в ожидании. Установщики (и системный, и сторонние)
/// сплошь и рядом устроены так: запущенный процесс распаковывает настоящий деинсталлятор,
/// передаёт ему работу и немедленно завершается. Наивное ожидание выхода вернуло бы
/// управление через секунду, поиск остатков пошёл бы по ещё не удалённой программе
/// и не нашёл бы ничего.
///
/// Поэтому процесс помещается в объект задания, и ожидание идёт до того момента,
/// когда в задании не останется ни одного живого процесса — включая всех потомков.
/// </remarks>
public sealed class UninstallRunner
{
    /// <summary>Запустить удаление и дождаться завершения всего дерева процессов.</summary>
    /// <param name="app">Программа.</param>
    /// <param name="silent">
    /// Пытаться удалить без вопросов. Работает, только если программа сама объявила
    /// такую команду: подставлять ключи тихого режима наугад нельзя — у разных
    /// установщиков они разные, и неверный ключ приводит к тому, что окно всё равно
    /// появляется, а иногда к запуску совсем другого сценария.
    /// </param>
    /// <param name="timeout">Предел ожидания.</param>
    /// <param name="ct">Отмена.</param>
    public async Task<UninstallOutcome> RunAsync(
        InstalledApp app,
        bool silent,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(app);

        var command = silent && !string.IsNullOrWhiteSpace(app.QuietUninstallCommand)
            ? app.QuietUninstallCommand
            : app.UninstallCommand;

        if (string.IsNullOrWhiteSpace(command))
        {
            return new UninstallOutcome(UninstallStatus.NoCommand, null,
                "Программа не сообщила системе, как её удалять");
        }

        var (executable, arguments) = SplitCommand(command);

        if (!File.Exists(executable) && Path.IsPathRooted(executable))
        {
            // Запись в реестре осталась, а самого деинсталлятора уже нет.
            // Это частый случай, и именно для него существует принудительное удаление.
            return new UninstallOutcome(UninstallStatus.ExecutableMissing, null,
                $"Деинсталлятор не найден: {executable}");
        }

        using var job = JobObject.Create();

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(executable) ?? Environment.CurrentDirectory,
            });

            if (process is null)
            {
                return new UninstallOutcome(UninstallStatus.Failed, null, "Не удалось запустить деинсталлятор");
            }

            job?.Assign(process);

            var completed = await WaitForTreeAsync(process, job, timeout, ct).ConfigureAwait(false);

            if (!completed)
            {
                return new UninstallOutcome(UninstallStatus.TimedOut, null,
                    "Удаление не завершилось за отведённое время. Возможно, деинсталлятор ждёт ответа пользователя");
            }

            var exitCode = SafeExitCode(process);

            return exitCode is 0 or null
                ? new UninstallOutcome(UninstallStatus.Completed, exitCode, null)
                : new UninstallOutcome(UninstallStatus.Completed, exitCode,
                    $"Деинсталлятор завершился с кодом {exitCode}. Проверьте, удалилась ли программа");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Отказ пользователя в запросе прав приходит именно сюда.
            return new UninstallOutcome(UninstallStatus.Failed, null, ex.Message);
        }
    }

    private static async Task<bool> WaitForTreeAsync(Process process, JobObject? job, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;

        // Ждём выхода самого запущенного процесса.
        while (!process.HasExited)
        {
            if (DateTime.UtcNow > deadline || ct.IsCancellationRequested)
            {
                return false;
            }

            await Task.Delay(200, ct).ConfigureAwait(false);
        }

        if (job is null)
        {
            return true;
        }

        // А затем — пока не опустеет всё дерево: настоящая работа обычно идёт в потомке.
        while (job.ActiveProcessCount > 0)
        {
            if (DateTime.UtcNow > deadline || ct.IsCancellationRequested)
            {
                return false;
            }

            await Task.Delay(300, ct).ConfigureAwait(false);
        }

        return true;
    }

    private static int? SafeExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Разобрать команду из реестра на исполняемый файл и аргументы.</summary>
    internal static (string Executable, string Arguments) SplitCommand(string command)
    {
        var trimmed = command.Trim();

        // Путь в кавычках: всё до закрывающей кавычки — файл.
        if (trimmed.StartsWith('"'))
        {
            var closing = trimmed.IndexOf('"', 1);

            if (closing > 0)
            {
                return (trimmed[1..closing], trimmed[(closing + 1)..].Trim());
            }
        }

        // Без кавычек путь может содержать пробелы, поэтому ищем самый длинный
        // существующий префикс, оканчивающийся на .exe.
        var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);

        if (exeIndex > 0)
        {
            var end = exeIndex + 4;
            return (trimmed[..end], trimmed[end..].Trim());
        }

        var space = trimmed.IndexOf(' ');

        return space > 0
            ? (trimmed[..space], trimmed[(space + 1)..].Trim())
            : (trimmed, string.Empty);
    }
}

/// <param name="Status">Чем закончилось.</param>
/// <param name="ExitCode">Код возврата деинсталлятора, если его удалось получить.</param>
/// <param name="Message">Пояснение для пользователя человеческим языком.</param>
public sealed record UninstallOutcome(UninstallStatus Status, int? ExitCode, string? Message);

public enum UninstallStatus
{
    Completed,

    /// <summary>Программа не сообщила команду удаления.</summary>
    NoCommand,

    /// <summary>Команда есть, а файла деинсталлятора нет — случай для принудительного удаления.</summary>
    ExecutableMissing,

    TimedOut,
    Failed,
}

/// <summary>
/// Объект задания Windows: позволяет дождаться завершения всего дерева процессов.
/// </summary>
internal sealed class JobObject : IDisposable
{
    private readonly IntPtr _handle;

    private JobObject(IntPtr handle) => _handle = handle;

    public static JobObject? Create()
    {
        var handle = CreateJobObjectW(IntPtr.Zero, null);
        return handle == IntPtr.Zero ? null : new JobObject(handle);
    }

    public void Assign(Process process)
    {
        try
        {
            AssignProcessToJobObject(_handle, process.Handle);
        }
        catch (InvalidOperationException)
        {
            // Процесс мог завершиться раньше, чем мы успели его назначить.
        }
    }

    /// <summary>Сколько процессов задания ещё живо.</summary>
    public int ActiveProcessCount
    {
        get
        {
            var info = new JOBOBJECT_BASIC_ACCOUNTING_INFORMATION();
            var size = Marshal.SizeOf(info);
            var buffer = Marshal.AllocHGlobal(size);

            try
            {
                Marshal.StructureToPtr(info, buffer, false);

                if (!QueryInformationJobObject(_handle, JobObjectBasicAccountingInformation, buffer, (uint)size, IntPtr.Zero))
                {
                    return 0;
                }

                var result = Marshal.PtrToStructure<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(buffer);
                return (int)result.ActiveProcesses;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            CloseHandle(_handle);
        }
    }

    private const int JobObjectBasicAccountingInformation = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        IntPtr hJob,
        int jobObjectInformationClass,
        IntPtr lpJobObjectInformation,
        uint cbJobObjectInformationLength,
        IntPtr lpReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
