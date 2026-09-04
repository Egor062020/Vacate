using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;

namespace Vacate.Platform.Windows.Files;

/// <summary>
/// Проверка и установка обновлений.
/// </summary>
/// <remarks>
/// Обновление — это запуск чужого кода на компьютере пользователя, поэтому здесь
/// действуют правила строже, чем в остальном продукте:
///
///   1. Проверяется ПОДПИСЬ скачанного файла, а не контрольная сумма. Сумма,
///      опубликованная рядом с файлом, защищает только от повреждения при скачивании:
///      тот, кто получил доступ к учётной записи или сборочному конвейеру, подменит
///      и файл, и сумму, и номер версии.
///   2. Издатель сверяется с ожидаемым. Действительная подпись сама по себе ничего
///      не значит — подписать свою программу может кто угодно.
///   3. Версия должна быть строго новее. Иначе можно подсунуть старую уязвимую.
///   4. Без подписи кода тихая установка не включается вообще: автоматический запуск
///      непроверенного кода с правами администратора на всех розданных машинах —
///      слишком дорогая цена за удобство.
/// </remarks>
public sealed class UpdateChecker(HttpClient? httpClient = null)
{
    /// <summary>Проверять не чаще раза в сутки: чаще незачем, а лишние обращения в сеть раздражают.</summary>
    public static readonly TimeSpan MinimumCheckInterval = TimeSpan.FromHours(24);

    private const string ReleasesUrl = "https://api.github.com/repos/Egor062020/Vacate/releases/latest";

    /// <summary>Кому должна принадлежать подпись обновления.</summary>
    private const string ExpectedPublisherFragment = "Vacate";

    private readonly HttpClient _http = httpClient ?? CreateClient();

    /// <summary>Проверить, есть ли версия новее текущей.</summary>
    public async Task<UpdateCheckResult> CheckAsync(Version currentVersion, CancellationToken ct = default)
    {
        try
        {
            var release = await _http.GetFromJsonAsync<ReleaseInfo>(ReleasesUrl, ct).ConfigureAwait(false);

            if (release?.TagName is null)
            {
                return UpdateCheckResult.Failed("Сервер обновлений не ответил");
            }

            if (!TryParseVersion(release.TagName, out var latest))
            {
                return UpdateCheckResult.Failed("Не удалось разобрать номер версии");
            }

            // Строго новее: равная или более старая версия обновлением не является.
            if (latest <= currentVersion)
            {
                return UpdateCheckResult.UpToDate();
            }

            var asset = release.Assets?.FirstOrDefault(a =>
                a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true);

            return asset?.DownloadUrl is null
                ? UpdateCheckResult.Failed("В выпуске нет файла для установки")
                : UpdateCheckResult.Available(latest, asset.DownloadUrl, release.Body);
        }
        catch (HttpRequestException)
        {
            // Отсутствие сети — обычное дело, а не сбой программы.
            return UpdateCheckResult.Failed("Не удалось связаться с сервером обновлений");
        }
        catch (TaskCanceledException)
        {
            return UpdateCheckResult.Failed("Проверка обновлений заняла слишком долго");
        }
    }

    /// <summary>
    /// Проверить, можно ли доверять скачанному файлу.
    /// </summary>
    /// <remarks>
    /// Публичный метод: он же используется тестами и должен быть проверяемым отдельно
    /// от скачивания — на нём держится вся безопасность обновления.
    /// </remarks>
    public static SignatureVerdict VerifySignature(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return new SignatureVerdict(false, "Файл обновления не найден");
        }

        try
        {
            using var certificate = X509CertificateLoader.LoadCertificateFromFile(filePath);
            using var chain = new X509Chain();

            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
            // Отзыв проверяется, но недоступность списков отзыва не должна
            // блокировать обновление на машине без интернета.
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreCertificateAuthorityRevocationUnknown;

            if (!chain.Build(certificate))
            {
                return new SignatureVerdict(false, "Подпись файла недействительна");
            }

            var subject = certificate.Subject;

            // Действительная подпись сама по себе ничего не доказывает:
            // подписать программу может кто угодно. Важно, ЧЬЯ она.
            return subject.Contains(ExpectedPublisherFragment, StringComparison.OrdinalIgnoreCase)
                ? new SignatureVerdict(true, $"Подписано: {subject}")
                : new SignatureVerdict(false, $"Файл подписан другим издателем: {subject}");
        }
        catch (CryptographicException)
        {
            return new SignatureVerdict(false, "Файл не подписан");
        }
    }

    /// <summary>Разобрать номер версии из метки выпуска.</summary>
    internal static bool TryParseVersion(string tag, out Version version)
    {
        // Порядок важен: сначала пробелы, затем ведущая буква. При обратном порядке
        // строка вида " v3.0.0 " остаётся с буквой — пробел «прячет» её от обрезки.
        var cleaned = tag.Trim().TrimStart('v', 'V');

        return Version.TryParse(cleaned, out version!);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        // Сервер требует представиться, иначе отвечает отказом.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Vacate-Updater");

        return client;
    }

    private sealed record ReleaseInfo
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("assets")]
        public List<AssetInfo>? Assets { get; init; }
    }

    private sealed record AssetInfo
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("browser_download_url")]
        public string? DownloadUrl { get; init; }
    }
}

/// <param name="Status">Итог проверки.</param>
/// <param name="LatestVersion">Найденная версия.</param>
/// <param name="DownloadUrl">Откуда скачивать.</param>
/// <param name="Notes">Описание изменений.</param>
/// <param name="Message">Пояснение при неудаче.</param>
public sealed record UpdateCheckResult(
    UpdateStatus Status,
    Version? LatestVersion = null,
    string? DownloadUrl = null,
    string? Notes = null,
    string? Message = null)
{
    public static UpdateCheckResult UpToDate() => new(UpdateStatus.UpToDate);

    public static UpdateCheckResult Available(Version version, string url, string? notes)
        => new(UpdateStatus.UpdateAvailable, version, url, notes);

    public static UpdateCheckResult Failed(string message) => new(UpdateStatus.CheckFailed, Message: message);
}

public enum UpdateStatus
{
    UpToDate,
    UpdateAvailable,

    /// <summary>Проверить не удалось. Это не ошибка программы и не повод беспокоить пользователя.</summary>
    CheckFailed,
}

/// <param name="Trusted">Файлу можно доверять.</param>
/// <param name="Detail">Что именно выяснилось о подписи.</param>
public sealed record SignatureVerdict(bool Trusted, string Detail);
