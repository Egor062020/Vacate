using System.Runtime.InteropServices;

namespace Vacate.Platform.Windows.Files;

/// <summary>Системные вызовы Windows, нужные платформенному слою.</summary>
internal static class NativeMethods
{
    [Flags]
    private enum RecycleFlags : uint
    {
        NoConfirmation = 0x00000001,
        NoProgressUi = 0x00000002,
        NoSound = 0x00000004,
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, RecycleFlags dwFlags);

    /// <summary>Очистить Корзину указанного тома.</summary>
    public static bool EmptyRecycleBin(string? volumeRoot)
    {
        const int Ok = 0;
        const int ErrorNoRecycleBin = unchecked((int)0x8004010F);

        var result = SHEmptyRecycleBin(
            IntPtr.Zero,
            volumeRoot,
            RecycleFlags.NoConfirmation | RecycleFlags.NoProgressUi | RecycleFlags.NoSound);

        // Пустая корзина возвращает отдельный код — это успех, а не отказ.
        return result is Ok or ErrorNoRecycleBin;
    }
}

/// <summary>
/// Определение процессов, удерживающих файл.
/// </summary>
/// <remarks>
/// Использует диспетчер перезапуска — механизм, встроенный в Windows ровно для этой задачи
/// и почти никем не применяемый. Благодаря ему вместо бесполезного «файл занят другой программой»
/// пользователь видит имя конкретного приложения и может закрыть его осознанно,
/// не устанавливая для этого посторонних утилит.
/// </remarks>
internal static class FileLockInspector
{
    private const int RmRebootReasonNone = 0;
    private const int CchRmMaxAppName = 255;
    private const int CchRmMaxSvcName = 63;
    private const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxAppName + 1)]
        public string strAppName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CchRmMaxSvcName + 1)]
        public string strServiceShortName;

        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(
        uint pSessionHandle,
        uint nFiles,
        string[] rgsFilenames,
        uint nApplications,
        RM_UNIQUE_PROCESS[]? rgApplications,
        uint nServices,
        string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(
        uint dwSessionHandle,
        out uint pnProcInfoNeeded,
        ref uint pnProcInfo,
        [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
        ref uint lpdwRebootReasons);

    /// <summary>
    /// Кто держит файл. Возвращает пустую ссылку, если определить не удалось —
    /// придумывать имя процесса нельзя, честнее промолчать.
    /// </summary>
    public static string? DescribeHolder(string path)
    {
        uint sessionHandle = 0;

        try
        {
            var key = Guid.NewGuid().ToString("N");

            if (RmStartSession(out sessionHandle, 0, key) != 0)
            {
                return null;
            }

            if (RmRegisterResources(sessionHandle, 1, [path], 0, null, 0, null) != 0)
            {
                return null;
            }

            uint procInfo = 0;
            uint rebootReasons = RmRebootReasonNone;

            var result = RmGetList(sessionHandle, out var needed, ref procInfo, null, ref rebootReasons);

            if (result != ErrorMoreData || needed == 0)
            {
                return null;
            }

            var processes = new RM_PROCESS_INFO[needed];
            procInfo = needed;

            if (RmGetList(sessionHandle, out _, ref procInfo, processes, ref rebootReasons) != 0)
            {
                return null;
            }

            var names = processes
                .Take((int)procInfo)
                .Select(p => string.IsNullOrWhiteSpace(p.strAppName)
                    ? $"процесс {p.Process.dwProcessId}"
                    : $"{p.strAppName} ({p.Process.dwProcessId})")
                .Distinct()
                .ToArray();

            return names.Length == 0 ? null : string.Join(", ", names);
        }
        catch (DllNotFoundException)
        {
            return null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (sessionHandle != 0)
            {
                RmEndSession(sessionHandle);
            }
        }
    }
}
