using Vacate.Abstractions.Execution;
using Vacate.Abstractions.Model;

namespace Vacate.Core.Execution;

/// <summary>
/// Приёмник действий для сухого прогона: ничего не меняет, только записывает намерения.
/// </summary>
/// <remarks>
/// Это и есть весь механизм предпросмотра. Отдельной ветки кода «если предпросмотр,
/// то не удалять» в продукте нет намеренно — именно такая ветка со временем расходится
/// с боевой, и предпросмотр начинает врать. Здесь же и охрана, и подсчёт объёмов,
/// и журналирование выполняются тем же кодом, что при реальной работе.
///
/// Исполнитель не знает, какой приёмник ему подставлен, и не имеет способа это выяснить.
/// Флаг «сейчас предпросмотр», который каждый исполнитель обязан не забыть проверить,
/// рано или поздно забудут — и предпросмотр удалит файлы по-настоящему.
/// </remarks>
public sealed class RecordingEffectSink : IEffectSink
{
    private readonly List<RecordedEffect> _recorded = [];

    /// <summary>Всё, что было бы сделано.</summary>
    public IReadOnlyList<RecordedEffect> Recorded => _recorded;

    public Task<EffectOutcome> DeleteFileAsync(FileTarget target, DeleteDisposition disposition, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _recorded.Add(new RecordedEffect(RecordedEffectKind.DeleteFile, target.Path, target.SizeOnDiskBytes, disposition.ToString()));

        // В предпросмотре считаем, что операция удалась: пользователю показывается
        // оценка сверху, а не заниженная цифра.
        return Task.FromResult(EffectOutcome.Success(target.SizeOnDiskBytes));
    }

    public Task<EffectOutcome> DeleteRegistryAsync(RegistryTarget target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _recorded.Add(new RecordedEffect(
            RecordedEffectKind.DeleteRegistry,
            DescribeRegistry(target),
            0,
            target.IsWholeKey ? "ключ целиком" : "значение"));

        return Task.FromResult(EffectOutcome.Success(0));
    }

    public Task<EffectOutcome> SetRegistryValueAsync(RegistryTarget target, RegistryValueData value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _recorded.Add(new RecordedEffect(RecordedEffectKind.SetRegistry, DescribeRegistry(target), 0, value.Kind.ToString()));
        return Task.FromResult(EffectOutcome.Success(0));
    }

    public Task<EffectOutcome> EmptyRecycleBinAsync(string volumeRoot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _recorded.Add(new RecordedEffect(RecordedEffectKind.EmptyRecycleBin, volumeRoot, 0, null));
        return Task.FromResult(EffectOutcome.Success(0));
    }

    private static string DescribeRegistry(RegistryTarget target)
    {
        var view = target.View == RegistryViewKind.Registry32 ? " (32-разрядное представление)" : string.Empty;
        var value = target.ValueName is null ? string.Empty : $" :: {target.ValueName}";
        return $"{target.Hive}\\{target.SubKeyPath}{value}{view}";
    }
}

/// <param name="Kind">Что было бы сделано.</param>
/// <param name="Target">С чем.</param>
/// <param name="SizeOnDiskBytes">Сколько места это занимает.</param>
/// <param name="Detail">Подробность: способ удаления, тип значения.</param>
public sealed record RecordedEffect(RecordedEffectKind Kind, string Target, long SizeOnDiskBytes, string? Detail);

public enum RecordedEffectKind
{
    DeleteFile,
    DeleteRegistry,
    SetRegistry,
    EmptyRecycleBin,
}
