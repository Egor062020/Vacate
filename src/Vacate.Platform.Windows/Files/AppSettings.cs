using System.Text.Json;

namespace Vacate.Platform.Windows.Files;

/// <summary>
/// Настройки программы. Хранятся в профиле пользователя рядом с журналом.
/// </summary>
/// <remarks>
/// Появились ради одного обещания, данного в политике подписи: проверка обновлений
/// «полностью отключается в настройках». Обещание, для которого нет способа его исполнить,
/// хуже отсутствия обещания — поэтому сначала место для настройки, потом проверка.
///
/// Формат простой и читаемый человеком намеренно: файл лежит в его профиле, и он вправе
/// открыть его блокнотом. Повреждённый файл не считается ошибкой — берутся значения
/// по умолчанию, а не отказ запускаться.
/// </remarks>
public sealed record AppSettings
{
    /// <summary>Проверять наличие новой версии.</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>Когда проверяли в последний раз. Чаще раза в сутки незачем.</summary>
    public DateTime? LastUpdateCheckUtc { get; init; }

    /// <summary>Версия, о которой человек попросил больше не напоминать.</summary>
    public string? DismissedVersion { get; init; }

    /// <summary>Держать значок в области уведомлений.</summary>
    public bool ShowTrayIcon { get; init; }

    /// <summary>
    /// Закрытие окна прячет программу в область уведомлений вместо выхода.
    /// </summary>
    /// <remarks>
    /// По умолчанию выключено. Программа, которая не закрывается по кнопке «закрыть»,
    /// воспринимается как навязчивая — такое поведение человек должен включить сам.
    /// </remarks>
    public bool MinimizeToTray { get; init; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vacate",
        "settings.json");

    /// <summary>Прочитать настройки. Отсутствие или повреждение файла даёт значения по умолчанию.</summary>
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Настройки — удобство, а не условие работы. Испорченный файл
            // не должен мешать человеку пользоваться программой.
            return new AppSettings();
        }
    }

    /// <summary>Сохранить настройки.</summary>
    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(FilePath);

            if (directory is not null)
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не сохранилось — переживём. Ронять программу из-за настроек нельзя.
        }
    }

    /// <summary>Пора ли снова спрашивать сервер об обновлениях.</summary>
    public bool ShouldCheckNow(DateTime utcNow) =>
        CheckForUpdates
        && (LastUpdateCheckUtc is null || utcNow - LastUpdateCheckUtc.Value >= UpdateChecker.MinimumCheckInterval);
}
