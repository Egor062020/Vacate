using System.Diagnostics;

namespace Vacate.Platform.Windows.Files;

/// <summary>
/// Проверка целостности системных файлов штатными средствами Windows.
/// </summary>
/// <remarks>
/// Тонкости, из-за которых наивная реализация не работает:
///
///   1. Проверку нельзя прервать. Она идёт от десяти минут до сорока и переживёт
///      закрытие окна программы. Поэтому интерфейс обязан блокировать выход,
///      а не делать вид, что операцию можно отменить.
///   2. Вывод проверки локализован и печатается с возвратом каретки для показа
///      процентов. Разбирать его текстом — значит написать разбор, который работает
///      на своей машине и ломается на системе с другим языком. Итог берётся
///      из системного журнала обслуживания: он всегда на английском.
///   3. Восстановление хранилища компонентов без доступа к серверам обновлений
///      завершается отказом. Это не ошибка программы, и сообщать надо именно это,
///      а не «произошла ошибка».
/// </remarks>
public sealed class SystemIntegrityChecker
{
    /// <summary>Журнал обслуживания, откуда берётся достоверный итог проверки.</summary>
    private static string ServicingLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Logs", "CBS", "CBS.log");

    /// <summary>Запустить проверку целостности и дождаться результата.</summary>
    /// <param name="progress">Куда сообщать о ходе работы.</param>
    /// <param name="ct">Отмена ожидания. Саму проверку прервать невозможно.</param>
    public async Task<IntegrityCheckResult> RunAsync(IProgress<string>? progress, CancellationToken ct = default)
    {
        if (!IsElevated())
        {
            return new IntegrityCheckResult(
                IntegrityStatus.NeedsElevation,
                "Проверка целостности возможна только с правами администратора");
        }

        progress?.Report("Проверка идёт. Обычно занимает от 10 до 40 минут, прерывать нельзя.");

        var startedAt = DateTime.Now;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.System),
                    "sfc.exe"),
                Arguments = "/scannow",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return new IntegrityCheckResult(IntegrityStatus.Failed, "Не удалось запустить проверку");
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            // Код возврата у этой проверки не различает «всё цело» и «нашли и починили»,
            // поэтому итог читается из журнала обслуживания.
            return await ReadOutcomeAsync(startedAt, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new IntegrityCheckResult(
                IntegrityStatus.StillRunning,
                "Ожидание прервано, но сама проверка продолжает работать в фоне и её нельзя остановить");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            return new IntegrityCheckResult(IntegrityStatus.Failed, ex.Message);
        }
    }

    /// <summary>Прочитать итог из журнала обслуживания.</summary>
    private static async Task<IntegrityCheckResult> ReadOutcomeAsync(DateTime startedAt, CancellationToken ct)
    {
        if (!File.Exists(ServicingLogPath))
        {
            return new IntegrityCheckResult(IntegrityStatus.Unknown, "Журнал обслуживания недоступен, итог узнать не удалось");
        }

        try
        {
            // Журнал большой, поэтому читаем поток и держим только последние строки нужного вида.
            var recent = new Queue<string>();

            using var stream = new FileStream(ServicingLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                // Записи проверки целостности помечены собственным префиксом.
                if (line.Contains("[SR]", StringComparison.Ordinal))
                {
                    recent.Enqueue(line);

                    if (recent.Count > 400)
                    {
                        recent.Dequeue();
                    }
                }
            }

            var lines = recent.ToList();

            // Формулировки в журнале всегда английские независимо от языка системы.
            var repaired = lines.Count(l => l.Contains("Repairing", StringComparison.OrdinalIgnoreCase)
                                            || l.Contains("Repaired", StringComparison.OrdinalIgnoreCase));

            var couldNotRepair = lines.Any(l => l.Contains("cannot repair", StringComparison.OrdinalIgnoreCase)
                                                || l.Contains("unable to repair", StringComparison.OrdinalIgnoreCase));

            if (couldNotRepair)
            {
                return new IntegrityCheckResult(
                    IntegrityStatus.DamageFound,
                    "Найдены повреждения, которые не удалось исправить. Требуется восстановление хранилища компонентов",
                    repaired);
            }

            if (repaired > 0)
            {
                return new IntegrityCheckResult(
                    IntegrityStatus.Repaired,
                    $"Повреждения найдены и исправлены (записей о восстановлении: {repaired})",
                    repaired);
            }

            return new IntegrityCheckResult(IntegrityStatus.Clean, "Нарушений целостности не обнаружено");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new IntegrityCheckResult(IntegrityStatus.Unknown, "Не удалось прочитать журнал обслуживания");
        }
    }

    /// <summary>Запущена ли программа с правами администратора.</summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);

            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <param name="Status">Чем закончилась проверка.</param>
/// <param name="Message">Пояснение человеческим языком.</param>
/// <param name="RepairedCount">Сколько записей о восстановлении найдено в журнале.</param>
public sealed record IntegrityCheckResult(IntegrityStatus Status, string Message, int RepairedCount = 0);

/// <summary>
/// Итог проверки в виде, пригодном для передачи между процессами.
/// </summary>
/// <remarks>
/// Проверка требует прав администратора, а окно программы работает без них, поэтому
/// её запускает отдельный процесс. Состояние передаётся строкой, а не числом:
/// файл отчёта переживает обновление программы, а порядок значений в перечислении — нет.
/// </remarks>
public sealed record IntegrityReport(string Status, string Message);

public enum IntegrityStatus
{
    Clean,
    Repaired,
    DamageFound,
    NeedsElevation,

    /// <summary>Ожидание прервано, но проверка продолжает работать: остановить её нельзя.</summary>
    StillRunning,

    Unknown,
    Failed,
}
