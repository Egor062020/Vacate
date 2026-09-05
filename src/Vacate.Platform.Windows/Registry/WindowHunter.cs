using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Vacate.Abstractions.Model;

namespace Vacate.Platform.Windows.Registry;

/// <summary>
/// Режим охотника: найти, чем установлена программа, окно которой человек видит.
/// </summary>
/// <remarks>
/// Задача, которую иначе не решить. В списке установленного программа называется так,
/// как её назвал издатель, а не так, как написано в её окне: «Разработка» может
/// оказаться «JetBrains Rider», а безымянное окно с рекламой — «Driver Booster PRO».
/// Человек видит окно и хочет удалить именно это, а найти в списке не может.
///
/// Ход рассуждения, от надёжного к предположительному:
///
///   1. По окну определяется процесс, по процессу — исполняемый файл. Это точные данные,
///      их даёт система.
///   2. Файл ищется среди каталогов установки известных программ. Совпадение пути —
///      надёжное основание: программа сама сообщила, где живёт.
///   3. Если не нашлось, сравниваются издатель из подписи файла и название окна.
///      Это уже догадка, и она помечается как догадка.
///
/// Системные окна исключаются сразу: за рабочим столом стоит процесс проводника,
/// и предложение «удалить программу этого окна» для него было бы вредным советом.
/// </remarks>
public sealed class WindowHunter
{
    /// <summary>Процессы, которым не место в предложении «что удалить».</summary>
    private static readonly HashSet<string> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "csrss", "winlogon", "services", "lsass", "svchost",
        "taskmgr", "sihost", "ctfmon", "ShellExperienceHost", "SearchHost",
        "StartMenuExperienceHost", "TextInputHost", "ApplicationFrameHost",
    };

    /// <summary>Определить программу по окну под указанной точкой экрана.</summary>
    /// <param name="screenX">Координата X на экране.</param>
    /// <param name="screenY">Координата Y на экране.</param>
    public HuntResult HuntAt(int screenX, int screenY)
    {
        var handle = WindowFromPoint(new POINT { X = screenX, Y = screenY });

        return handle == IntPtr.Zero
            ? new HuntResult(null, null, null, "Под курсором нет окна")
            : Identify(handle);
    }

    /// <summary>Определить программу по её окну.</summary>
    internal HuntResult Identify(IntPtr windowHandle)
    {
        // Берём корневое окно: нажатие обычно попадает в кнопку или панель,
        // а нужен процесс всего окна.
        var root = GetAncestor(windowHandle, GA_ROOTOWNER);

        if (root != IntPtr.Zero)
        {
            windowHandle = root;
        }

        _ = GetWindowThreadProcessId(windowHandle, out var processId);

        if (processId == 0)
        {
            return new HuntResult(null, null, null, "Не удалось определить программу этого окна");
        }

        string? executable;
        string processName;

        try
        {
            using var process = Process.GetProcessById((int)processId);

            processName = process.ProcessName;
            executable = QueryExecutablePath(processId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return new HuntResult(null, null, null, "Программа уже закрылась");
        }

        if (SystemProcesses.Contains(processName))
        {
            return new HuntResult(null, executable, ReadWindowTitle(windowHandle),
                $"Это окно принадлежит системе ({processName}). Удалять его не нужно");
        }

        if (executable is null)
        {
            return new HuntResult(null, null, ReadWindowTitle(windowHandle),
                "Не удалось узнать, каким файлом запущено это окно");
        }

        return Match(executable, ReadWindowTitle(windowHandle));
    }

    /// <summary>Сопоставить исполняемый файл со списком установленных программ.</summary>
    private static HuntResult Match(string executable, string? title)
    {
        var apps = new InstalledAppsScanner().Scan();

        // Совпадение по каталогу установки: программа сама сообщила, где живёт.
        foreach (var app in apps.Where(a => !string.IsNullOrWhiteSpace(a.InstallLocation)))
        {
            var location = app.InstallLocation!.TrimEnd(Path.DirectorySeparatorChar);

            if (executable.StartsWith(location + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return new HuntResult(app, executable, title, null);
            }
        }

        // Догадка по издателю из подписи файла. Помечается как догадка,
        // потому что у одного издателя программ бывает много.
        var publisher = ReadPublisher(executable);

        if (publisher is not null)
        {
            var byPublisher = apps
                .Where(a => a.Publisher is not null
                            && a.Publisher.Contains(publisher, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (byPublisher.Count == 1)
            {
                return new HuntResult(byPublisher[0], executable, title,
                    $"Определено по издателю «{publisher}» — проверьте, та ли это программа");
            }
        }

        return new HuntResult(null, executable, title,
            "Эта программа не значится в списке установленного. Возможно, она работает без установки");
    }

    private static string? ReadPublisher(string executable)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(executable);

            return string.IsNullOrWhiteSpace(info.CompanyName) ? null : info.CompanyName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadWindowTitle(IntPtr handle)
    {
        var length = GetWindowTextLength(handle);

        if (length <= 0)
        {
            return null;
        }

        var buffer = new StringBuilder(length + 1);
        GetWindowText(handle, buffer, buffer.Capacity);

        return buffer.ToString();
    }

    /// <summary>
    /// Путь к файлу процесса.
    /// </summary>
    /// <remarks>
    /// Через системный запрос, а не через свойства процесса: для программ,
    /// запущенных с другими правами, обычный путь недоступен и даёт отказ.
    /// </remarks>
    private static string? QueryExecutablePath(uint processId)
    {
        const int QueryLimitedInformation = 0x1000;

        var handle = OpenProcess(QueryLimitedInformation, false, processId);

        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new StringBuilder(1024);
            var size = buffer.Capacity;

            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint GA_ROOTOWNER = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder buffer, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

/// <param name="App">Найденная программа. Пустая ссылка, если сопоставить не удалось.</param>
/// <param name="ExecutablePath">Файл, которым запущено окно.</param>
/// <param name="WindowTitle">Заголовок окна — то, что человек видит на экране.</param>
/// <param name="Note">Оговорка или причина неудачи. Показывается человеку как есть.</param>
public sealed record HuntResult(InstalledApp? App, string? ExecutablePath, string? WindowTitle, string? Note);
