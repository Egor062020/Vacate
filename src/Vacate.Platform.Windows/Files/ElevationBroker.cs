using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using Vacate.Abstractions.Model;

namespace Vacate.Platform.Windows.Files;

/// <summary>
/// Выполняет операции, требующие прав администратора, в отдельном процессе.
/// </summary>
/// <remarks>
/// Интерфейс продукта работает БЕЗ повышения прав, и это решение принято сознательно.
/// Требовать администратора для всего процесса означало бы: сетевые диски пользователя
/// становятся невидимыми, перетаскивание файлов из проводника перестаёт работать,
/// удаление уходит в корзину администратора вместо корзины человека, а тот, у кого
/// пароля администратора нет, не запустит программу вовсе — хотя половина её работы
/// прав не требует.
///
/// Поэтому здесь принят обратный подход: интерфейс безправный, а разрушающая работа,
/// которой права действительно нужны, передаётся отдельному процессу. Побочная выгода
/// весомее удобства: у процесса с интерфейсом ФИЗИЧЕСКИ нет прав что-либо удалить
/// в системных каталогах, даже если в нём есть ошибка.
///
/// План передаётся через временный файл, а не через командную строку: строка ограничена
/// по длине, видна в списке процессов целиком и не переживает пути с кавычками.
/// </remarks>
public sealed class ElevationBroker
{
    /// <summary>Нужны ли права администратора для выполнения плана.</summary>
    public static bool RequiresElevation(MutationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        foreach (var operation in plan.AllOperations)
        {
            switch (operation)
            {
                // Общесистемная ветка реестра пользователю недоступна.
                case DeleteRegistryOperation { Target.Hive: RegistryHiveKind.LocalMachine }:
                case SetRegistryValueOperation { Target.Hive: RegistryHiveKind.LocalMachine }:
                    return true;

                case DeleteFileOperation file when IsSystemArea(file.Target.Path):
                    return true;
            }
        }

        return false;
    }

    /// <summary>Уже запущены с правами администратора.</summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Выполнить план в отдельном процессе с правами администратора.
    /// </summary>
    /// <param name="plan">План.</param>
    /// <param name="executorPath">Путь к исполнителю (консольной версии продукта).</param>
    /// <param name="ct">Отмена ожидания.</param>
    public async Task<ElevationOutcome> ExecuteElevatedAsync(
        MutationPlan plan,
        string executorPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(executorPath);

        if (!File.Exists(executorPath))
        {
            return new ElevationOutcome(false, "Исполнитель не найден");
        }

        var planPath = Path.Combine(Path.GetTempPath(), $"vacate-plan-{Guid.NewGuid():N}.json");
        var reportPath = Path.Combine(Path.GetTempPath(), $"vacate-report-{Guid.NewGuid():N}.json");

        try
        {
            await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan), ct).ConfigureAwait(false);

            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executorPath,
                Arguments = $"--execute-plan \"{planPath}\" --report \"{reportPath}\"",

                // Запрос повышения прав через оболочку: система показывает
                // штатное окно подтверждения. Это не обход проверки, а её вызов.
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            if (process is null)
            {
                return new ElevationOutcome(false, "Не удалось запустить исполнителя");
            }

            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var report = await ReadReportAsync(reportPath, ct).ConfigureAwait(false);

            if (process.ExitCode == 0)
            {
                return new ElevationOutcome(true, "Выполнено", report);
            }

            // Причина отказа приходит из отчёта: код возврата человеку ничего не говорит.
            return new ElevationOutcome(
                false,
                report?.Error ?? $"Исполнитель завершился с кодом {process.ExitCode}",
                report);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Пользователь отказался в окне подтверждения. Это его право,
            // а не сбой: сообщение должно быть спокойным.
            return new ElevationOutcome(false, "Вы отказались предоставить права администратора");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new ElevationOutcome(false, ex.Message);
        }
        finally
        {
            // План содержит пути пользователя — не оставляем его во временной папке.
            Discard(planPath);
            Discard(reportPath);
        }
    }

    private static async Task<ElevatedRunReport?> ReadReportAsync(string path, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<ElevatedRunReport>(
                await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Отчёта нет или он повреждён. Само выполнение это не отменяет,
            // и сказать об этом честнее, чем показать выдуманные цифры.
            return null;
        }
    }

    private static void Discard(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Исполнитель мог ещё держать файл. Он будет убран обычной очисткой.
        }
    }

    /// <summary>Лежит ли путь в области, недоступной обычному пользователю.</summary>
    internal static bool IsSystemArea(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(path);

            var systemAreas = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            };

            return systemAreas.Any(area =>
                !string.IsNullOrEmpty(area)
                && full.StartsWith(area + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}

/// <param name="Success">Операция выполнена.</param>
/// <param name="Message">Пояснение человеческим языком.</param>
/// <param name="Report">
/// Что именно сделал поднятый процесс. Пустая ссылка, если он не успел отчитаться.
/// </param>
public sealed record ElevationOutcome(bool Success, string Message, ElevatedRunReport? Report = null);

/// <summary>
/// Отчёт поднятого процесса.
/// </summary>
/// <remarks>
/// Отдельный тип, а не полный отчёт исполнения: через границу процессов передаётся
/// только то, что интерфейс покажет человеку. Без этих цифр честный счётчик показывал бы
/// ноль после каждой операции с повышением прав — то есть врал бы ровно там,
/// где работа была самой заметной.
/// </remarks>
/// <param name="SessionId">Сеанс в журнале: по нему выполняется откат.</param>
/// <param name="Error">Что помешало, если выполнение не состоялось.</param>
public sealed record ElevatedRunReport(
    int Succeeded,
    int Skipped,
    int Failed,
    int Denied,
    long ClaimedBytes,
    long ActuallyFreedBytes,
    string? SessionId,
    string? Error);
