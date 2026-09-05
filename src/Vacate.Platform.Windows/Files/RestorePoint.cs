using System.Runtime.InteropServices;

namespace Vacate.Platform.Windows.Files;

/// <summary>
/// Точка восстановления системы перед крупной операцией.
/// </summary>
/// <remarks>
/// Последний рубеж, когда не помогли ни карантин, ни копия ветвей реестра. Нужен редко,
/// но именно в том случае, ради которого продукт вообще стоит бояться: удалено то,
/// без чего система ведёт себя неправильно, а связь с нашей работой уже не очевидна.
///
/// Особенности, о которых обязан знать вызывающий:
///
///   1. Требуются права администратора. Без них система отвечает отказом,
///      и это не повод показывать ошибку — просто рубежа не будет.
///   2. Восстановление системы может быть отключено, и чаще всего оно отключено:
///      на большинстве установок Windows защита системного диска выключена.
///      Это состояние машины, а не сбой программы.
///   3. Windows не создаёт вторую точку чаще, чем раз в сутки, и молча возвращает успех.
///      Обещать человеку свежую точку после каждой очистки означало бы врать.
///
/// Поэтому метод возвращает честное состояние, а не «получилось / не получилось»:
/// разница между «точка создана» и «система их не делает» существенна для решения,
/// которое человек принимает дальше.
/// </remarks>
public sealed class RestorePoint
{
    /// <summary>
    /// Заслуживает ли план точки восстановления.
    /// </summary>
    /// <remarks>
    /// Не всякая работа с повышенными правами того стоит: очистка временных файлов
    /// в системном каталоге ничего необратимого не делает, а создание точки занимает
    /// время и место. Точка нужна там, где меняется устройство системы: общие ветви
    /// реестра и каталоги установленных программ.
    /// </remarks>
    public static bool IsWorthIt(Vacate.Abstractions.Model.MutationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        foreach (var operation in plan.AllOperations)
        {
            switch (operation)
            {
                case Vacate.Abstractions.Model.DeleteRegistryOperation
                {
                    Target.Hive: Vacate.Abstractions.Model.RegistryHiveKind.LocalMachine,
                }:
                    return true;

                // Каталог целиком в системной области — это удаление программы,
                // а не уборка мусора.
                case Vacate.Abstractions.Model.DeleteFileOperation { Target.IsDirectory: true } file
                    when ElevationBroker.IsSystemArea(file.Target.Path):
                    return true;
            }
        }

        return false;
    }

    /// <summary>Создать точку восстановления.</summary>
    /// <param name="description">Название, которое человек увидит в списке точек.</param>
    public RestorePointResult Create(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        if (!SystemIntegrityChecker.IsElevated())
        {
            return new RestorePointResult(RestorePointStatus.NeedsElevation,
                "Точку восстановления может создать только процесс с правами администратора");
        }

        if (!IsProtectionEnabled())
        {
            return new RestorePointResult(RestorePointStatus.Disabled,
                "Защита системы отключена — Windows не хранит точек восстановления. "
                + "Включить её можно в свойствах системы");
        }

        try
        {
            var info = new RESTOREPOINTINFO
            {
                dwEventType = BEGIN_SYSTEM_CHANGE,
                dwRestorePtType = APPLICATION_INSTALL,
                llSequenceNumber = 0,

                // Длина названия ограничена системой; обрезаем сами,
                // иначе вызов вернёт отказ без объяснения.
                szDescription = description.Length > 63 ? description[..63] : description,
            };

            if (!SRSetRestorePointW(ref info, out var status))
            {
                var error = Marshal.GetLastWin32Error();

                // Отказ по слишком частому созданию — не сбой: Windows сама
                // ограничивает частоту, и точка суточной давности тоже годится.
                const int ErrorServiceDisabled = 1058;

                return new RestorePointResult(
                    error == ErrorServiceDisabled ? RestorePointStatus.Disabled : RestorePointStatus.Failed,
                    $"Windows отказалась создать точку восстановления (код {error})");
            }

            // Начатое изменение нужно закрыть, иначе точка останется незавершённой
            // и в списке восстановления не появится.
            var end = new RESTOREPOINTINFO
            {
                dwEventType = END_SYSTEM_CHANGE,
                dwRestorePtType = APPLICATION_INSTALL,
                llSequenceNumber = status.llSequenceNumber,
                szDescription = info.szDescription,
            };

            SRSetRestorePointW(ref end, out _);

            return new RestorePointResult(RestorePointStatus.Created,
                "Точка восстановления создана", status.llSequenceNumber);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // На отдельных сборках Windows этой возможности нет вовсе.
            return new RestorePointResult(RestorePointStatus.Unavailable,
                "В этой версии Windows точки восстановления недоступны");
        }
    }

    /// <summary>Включена ли защита системного диска.</summary>
    internal static bool IsProtectionEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore");

            // Значение выставляется, когда защиту отключают явно. Его отсутствие
            // означает обычное состояние, а не запрет.
            return key?.GetValue("DisableSR") is not int disabled || disabled == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // Не смогли посмотреть — не считаем это запретом: пусть система ответит сама.
            return true;
        }
    }

    private const int BEGIN_SYSTEM_CHANGE = 100;
    private const int END_SYSTEM_CHANGE = 101;
    private const int APPLICATION_INSTALL = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RESTOREPOINTINFO
    {
        public int dwEventType;
        public int dwRestorePtType;
        public long llSequenceNumber;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STATEMGRSTATUS
    {
        public int nStatus;
        public long llSequenceNumber;
    }

    [DllImport("SrClient.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SRSetRestorePointW(ref RESTOREPOINTINFO pRestorePtSpec, out STATEMGRSTATUS pSMgrStatus);
}

/// <param name="Status">Чем закончилось.</param>
/// <param name="Message">Пояснение человеческим языком.</param>
/// <param name="SequenceNumber">Номер точки, если она создана.</param>
public sealed record RestorePointResult(RestorePointStatus Status, string Message, long SequenceNumber = 0);

public enum RestorePointStatus
{
    Created,

    /// <summary>Защита системы отключена — это состояние машины, а не сбой.</summary>
    Disabled,

    NeedsElevation,

    /// <summary>Возможности нет в этой версии Windows.</summary>
    Unavailable,

    Failed,
}
