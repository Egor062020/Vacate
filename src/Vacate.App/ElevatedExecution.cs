using System.IO;
using Vacate.Abstractions.Execution;
using Vacate.Abstractions.Model;
using Vacate.App.Views;
using Vacate.Core.Execution;
using Vacate.Core.Journal;
using Vacate.Core.Safety;
using Vacate.Platform.Windows.Files;
using Vacate.Platform.Windows.Registry;

namespace Vacate.App;

/// <summary>
/// Единственная точка, через которую интерфейс выполняет планы.
/// </summary>
/// <remarks>
/// Окно программы работает БЕЗ прав администратора, и это решение принято сознательно:
/// требовать их для всего процесса означало бы, что сетевые диски пользователя становятся
/// невидимыми, перетаскивание из проводника перестаёт работать, а удаление уходит в корзину
/// администратора вместо корзины человека.
///
/// Поэтому план, которому права действительно нужны, передаётся отдельному процессу,
/// а окно остаётся безправным. Побочная выгода весомее удобства: у процесса с интерфейсом
/// физически нет прав что-либо удалить в системных каталогах, даже если в нём есть ошибка.
///
/// Обе ветки возвращают один и тот же итог: страницам незачем знать, каким путём
/// выполнялась работа, и решать это каждой по-своему они не должны.
/// </remarks>
internal static class ElevatedExecution
{
    /// <summary>Консольная версия продукта рядом с окном: она же исполнитель поднятых планов.</summary>
    private static string ExecutorPath => Path.Combine(AppContext.BaseDirectory, "vacate-cli.exe");

    private static string JournalDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Vacate", "journal");

    /// <summary>Понадобится ли этому плану запрос прав администратора.</summary>
    /// <remarks>
    /// Знать это нужно ДО нажатия: окно системы появляется неожиданно, и человека
    /// стоит предупредить, что оно сейчас будет и почему.
    /// </remarks>
    public static bool WillAskForRights(MutationPlan plan, bool dryRun) =>
        !dryRun
        && ElevationBroker.RequiresElevation(plan)
        && !ElevationBroker.IsElevated()
        && File.Exists(ExecutorPath);

    /// <summary>Выполнить план, при необходимости запросив права.</summary>
    public static async Task<RunSummary> RunAsync(MutationPlan plan, bool dryRun, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // Предпросмотр ничего не меняет, значит и прав не требует никогда:
        // запрашивать их ради показа было бы враньём о намерениях.
        if (WillAskForRights(plan, dryRun))
        {
            var outcome = await new ElevationBroker()
                .ExecuteElevatedAsync(plan, ExecutorPath, ct)
                .ConfigureAwait(false);

            return RunSummary.FromElevated(outcome);
        }

        // Ветки реестра карантин не покрывает — их возвращает только выгрузка в файл.
        // Копия делается ДО удаления: после него сохранять уже нечего.
        var backup = dryRun ? null : await new RegistryBackup().SaveAsync(plan, ct).ConfigureAwait(false);

        var quarantine = new FileSystemQuarantine();
        var journal = new JsonlOperationJournal(JournalDirectory);
        var volumes = new VolumeInfoProvider();

        // Весь механизм предпросмотра — в выборе приёмника действий.
        IEffectSink sink = dryRun ? new RecordingEffectSink() : new RealEffectSink(quarantine);

        var executor = new PlanExecutor(
            sink,
            journal,
            volumes,
            new UiEnvironmentProvider(volumes),
            GuardSet.Group(CleanPage.BuildPolicy()),
            GuardSet.Item(),
            dryRun);

        var report = await executor.ExecuteAsync(plan, null, ct).ConfigureAwait(false);

        return RunSummary.FromReport(report, dryRun) with { RegistryBackupPath = backup?.Path };
    }
}

/// <summary>
/// Итог выполнения в виде, одинаковом для обоих путей.
/// </summary>
/// <param name="Elevated">Работу выполнил отдельный процесс с правами администратора.</param>
/// <param name="Error">
/// Что помешало. Заполнено, когда выполнения не было вовсе, — в том числе когда
/// человек отказал в правах, а это его право, а не сбой.
/// </param>
internal sealed record RunSummary(
    int Succeeded,
    int Skipped,
    int Failed,
    int Denied,
    long ClaimedBytes,
    long ActuallyFreedBytes,
    string? SessionId,
    bool WasDryRun,
    bool Elevated,
    string? Error,
    IReadOnlyList<DiscrepancyReason> Discrepancies)
{
    /// <summary>Файл с копией удалённых ветвей реестра, если они были.</summary>
    public string? RegistryBackupPath { get; init; }

    public static RunSummary FromReport(ExecutionReport report, bool dryRun) => new(
        report.Succeeded,
        report.Skipped,
        report.Failed,
        report.Denied,
        report.ClaimedBytes,
        report.ActuallyFreedBytes,
        report.SessionId,
        dryRun,
        Elevated: false,
        Error: null,
        report.Discrepancies);

    public static RunSummary FromElevated(ElevationOutcome outcome)
    {
        var report = outcome.Report;

        return new RunSummary(
            report?.Succeeded ?? 0,
            report?.Skipped ?? 0,
            report?.Failed ?? 0,
            report?.Denied ?? 0,
            report?.ClaimedBytes ?? 0,
            report?.ActuallyFreedBytes ?? 0,
            report?.SessionId,
            WasDryRun: false,
            Elevated: true,

            // Успех без отчёта — не повод показывать нули как результат:
            // человек должен понимать, что цифры не дошли, а не думать,
            // что работа была впустую.
            Error: outcome.Success
                ? report is null ? "Работа выполнена, но подробности от поднятого процесса не дошли" : null
                : outcome.Message,

            // Разбор расхождения через границу процессов не передаётся:
            // придумывать его здесь было бы хуже, чем не показать вовсе.
            [])
        {
            RegistryBackupPath = report?.RegistryBackupPath,
        };
    }
}
