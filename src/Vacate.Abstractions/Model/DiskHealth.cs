namespace Vacate.Abstractions.Model;

/// <summary>
/// Состояние физического диска.
/// </summary>
/// <param name="Model">Модель.</param>
/// <param name="MediaType">Тип носителя: твердотельный или с вращающимися дисками.</param>
/// <param name="SizeBytes">Объём.</param>
/// <param name="Health">Вердикт системы о состоянии.</param>
/// <param name="TemperatureCelsius">Температура, если датчик доступен.</param>
/// <param name="WearPercent">
/// Износ твердотельного накопителя в процентах. Величина осмысленна только для них.
/// </param>
/// <param name="PowerOnHours">Часы работы.</param>
/// <param name="ReadErrorsTotal">Накопленные ошибки чтения.</param>
/// <param name="Unavailable">
/// Чего именно узнать не удалось. Пустой список означает, что данные полны.
/// Показывать «здоровье 100%» там, где диск ничего не сообщил, — обман;
/// внешние диски через переходники SMART обычно не отдают вовсе.
/// </param>
public sealed record DiskHealth(
    string Model,
    string MediaType,
    long SizeBytes,
    DiskHealthStatus Health,
    int? TemperatureCelsius,
    int? WearPercent,
    long? PowerOnHours,
    long? ReadErrorsTotal,
    IReadOnlyList<string> Unavailable)
{
    /// <summary>Есть ли повод для беспокойства прямо сейчас.</summary>
    public bool NeedsAttention =>
        Health is DiskHealthStatus.Warning or DiskHealthStatus.Unhealthy
        || WearPercent >= 80
        || TemperatureCelsius >= 60;
}

public enum DiskHealthStatus
{
    /// <summary>Диск не сообщил о состоянии. Это не «всё хорошо», это «неизвестно».</summary>
    Unknown,

    Healthy,
    Warning,
    Unhealthy,
}
