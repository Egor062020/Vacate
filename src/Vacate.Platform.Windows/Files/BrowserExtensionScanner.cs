using System.Text.Json;
using Vacate.Abstractions.Model;

namespace Vacate.Platform.Windows.Files;

/// <summary>
/// Читает расширения, установленные в браузерах.
/// </summary>
/// <remarks>
/// Раздел сознательно ограничен показом. Прямая правка настроек в браузерах на движке
/// Chromium не имеет поддерживаемого способа: файл настроек защищён подписью, которую
/// браузер проверяет при запуске. Удаление каталога расширения тоже не работает как
/// удаление — браузер сообщит о повреждении, а при включённой синхронизации вернёт
/// расширение с сервера.
///
/// Поэтому ценность здесь в другом: показать, какие права расширение себе выпросило.
/// Список установленных расширений человек видит и в самом браузере, а вот что одно
/// из них читает содержимое всех страниц — нет.
///
/// Обрабатываются все профили браузера, а не только основной: у людей их обычно
/// несколько, и чистить только первый — типичная ошибка чужих утилит.
/// </remarks>
public sealed class BrowserExtensionScanner
{
    /// <summary>Браузеры на движке Chromium: профили лежат в локальных данных.</summary>
    private static readonly (string Name, string RelativePath)[] ChromiumBrowsers =
    [
        ("Chrome", @"Google\Chrome\User Data"),
        ("Edge", @"Microsoft\Edge\User Data"),
        ("Яндекс Браузер", @"Yandex\YandexBrowser\User Data"),
        ("Brave", @"BraveSoftware\Brave-Browser\User Data"),
        ("Vivaldi", @"Vivaldi\User Data"),
        ("Chromium", @"Chromium\User Data"),
    ];

    /// <summary>Собрать расширения из всех найденных браузеров.</summary>
    public IReadOnlyList<BrowserExtension> Scan(CancellationToken ct = default)
    {
        var result = new List<BrowserExtension>();
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        foreach (var (name, relative) in ChromiumBrowsers)
        {
            ct.ThrowIfCancellationRequested();
            ScanChromium(Path.Combine(localAppData, relative), name, result, ct);
        }

        // Opera держит профиль в перемещаемых данных, в отличие от остальных.
        ScanChromium(Path.Combine(roamingAppData, @"Opera Software\Opera Stable"), "Opera", result, ct);

        ScanFirefox(Path.Combine(roamingAppData, @"Mozilla\Firefox"), result, ct);

        return result
            .OrderByDescending(e => e.HighestLevel)
            .ThenBy(e => e.Browser)
            .ThenBy(e => e.Name)
            .ToList();
    }

    private void ScanChromium(string userDataPath, string browserName, List<BrowserExtension> result, CancellationToken ct)
    {
        if (!Directory.Exists(userDataPath))
        {
            return;
        }

        foreach (var profileDirectory in EnumerateChromiumProfiles(userDataPath))
        {
            ct.ThrowIfCancellationRequested();

            var extensionsRoot = Path.Combine(profileDirectory, "Extensions");

            if (!Directory.Exists(extensionsRoot))
            {
                continue;
            }

            var profileName = Path.GetFileName(profileDirectory);

            foreach (var extensionDirectory in SafeGetDirectories(extensionsRoot))
            {
                ct.ThrowIfCancellationRequested();

                var extensionId = Path.GetFileName(extensionDirectory);

                // Внутри — по каталогу на версию. Берём самую свежую.
                var versionDirectory = SafeGetDirectories(extensionDirectory)
                    .OrderByDescending(d => Directory.GetLastWriteTimeUtc(d))
                    .FirstOrDefault();

                if (versionDirectory is null)
                {
                    continue;
                }

                var extension = ReadChromiumExtension(versionDirectory, extensionId, browserName, profileName, ct);

                if (extension is not null)
                {
                    result.Add(extension);
                }
            }
        }
    }

    /// <summary>
    /// Профили браузера. Их бывает несколько, и обрабатывать нужно все.
    /// </summary>
    private static IEnumerable<string> EnumerateChromiumProfiles(string userDataPath)
    {
        foreach (var directory in SafeGetDirectories(userDataPath))
        {
            var name = Path.GetFileName(directory);

            if (name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
            {
                yield return directory;
            }
        }
    }

    private static BrowserExtension? ReadChromiumExtension(
        string versionDirectory,
        string extensionId,
        string browserName,
        string profileName,
        CancellationToken ct)
    {
        var manifestPath = Path.Combine(versionDirectory, "manifest.json");

        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;

            var name = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
            var version = root.TryGetProperty("version", out var versionElement) ? versionElement.GetString() : null;

            // Названия часто хранятся ссылкой на файл переводов, а не строкой.
            if (name is not null && name.StartsWith("__MSG_", StringComparison.Ordinal))
            {
                name = ResolveLocalizedName(versionDirectory, name) ?? extensionId;
            }

            var permissions = new List<ExtensionPermission>();
            CollectPermissions(root, "permissions", permissions);
            CollectPermissions(root, "optional_permissions", permissions);
            CollectPermissions(root, "host_permissions", permissions);
            CollectContentScriptMatches(root, permissions);

            return new BrowserExtension(
                Id: extensionId,
                Name: string.IsNullOrWhiteSpace(name) ? extensionId : name,
                Version: version,
                Browser: browserName,
                ProfileName: profileName,
                Permissions: permissions.DistinctBy(p => p.Raw).ToList(),
                SizeBytes: MeasureDirectory(versionDirectory, ct),
                Path: versionDirectory,
                LastUpdatedUtc: Directory.GetLastWriteTimeUtc(versionDirectory));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void CollectPermissions(JsonElement root, string propertyName, List<ExtensionPermission> destination)
    {
        if (!root.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } raw)
            {
                destination.Add(ExtensionPermissions.Translate(raw));
            }
        }
    }

    /// <summary>
    /// Адреса, на которых расширение выполняет свой код.
    /// </summary>
    /// <remarks>
    /// Их легко упустить: они лежат не в разрешениях, а в описании внедряемых сценариев.
    /// При этом именно они дают доступ к содержимому страниц — расширение может вовсе
    /// не просить «permissions», но выполнять свой код на всех сайтах.
    /// </remarks>
    private static void CollectContentScriptMatches(JsonElement root, List<ExtensionPermission> destination)
    {
        if (!root.TryGetProperty("content_scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var script in scripts.EnumerateArray())
        {
            if (!script.TryGetProperty("matches", out var matches) || matches.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var match in matches.EnumerateArray())
            {
                if (match.ValueKind == JsonValueKind.String && match.GetString() is { } raw)
                {
                    destination.Add(ExtensionPermissions.Translate(raw));
                }
            }
        }
    }

    private static string? ResolveLocalizedName(string versionDirectory, string messageKey)
    {
        // «__MSG_appName__» указывает на ключ appName в файле переводов.
        var key = messageKey.Trim('_')[4..].Trim('_');
        var localesRoot = Path.Combine(versionDirectory, "_locales");

        if (!Directory.Exists(localesRoot))
        {
            return null;
        }

        // Русский, затем английский, затем что найдётся.
        var candidates = new[] { "ru", "en", "en_US" }
            .Select(l => Path.Combine(localesRoot, l, "messages.json"))
            .Concat(SafeGetDirectories(localesRoot).Select(d => Path.Combine(d, "messages.json")));

        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (string.Equals(property.Name, key, StringComparison.OrdinalIgnoreCase)
                        && property.Value.TryGetProperty("message", out var message))
                    {
                        return message.GetString();
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                // Пробуем следующий язык.
            }
        }

        return null;
    }

    private void ScanFirefox(string firefoxRoot, List<BrowserExtension> result, CancellationToken ct)
    {
        var profilesRoot = Path.Combine(firefoxRoot, "Profiles");

        if (!Directory.Exists(profilesRoot))
        {
            return;
        }

        foreach (var profile in SafeGetDirectories(profilesRoot))
        {
            ct.ThrowIfCancellationRequested();

            var extensionsFile = Path.Combine(profile, "extensions.json");

            if (!File.Exists(extensionsFile))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(extensionsFile));

                if (!document.RootElement.TryGetProperty("addons", out var addons) || addons.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var addon in addons.EnumerateArray())
                {
                    var extension = ReadFirefoxAddon(addon, Path.GetFileName(profile));

                    if (extension is not null)
                    {
                        result.Add(extension);
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Профиль может быть занят работающим браузером.
            }
        }
    }

    private static BrowserExtension? ReadFirefoxAddon(JsonElement addon, string profileName)
    {
        // Встроенные компоненты браузера расширениями для пользователя не являются.
        if (addon.TryGetProperty("location", out var location)
            && location.GetString() is "app-builtin" or "app-system-defaults" or "app-global")
        {
            return null;
        }

        var id = addon.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;

        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        string? name = null;

        if (addon.TryGetProperty("defaultLocale", out var locale)
            && locale.TryGetProperty("name", out var nameElement))
        {
            name = nameElement.GetString();
        }

        var version = addon.TryGetProperty("version", out var versionElement) ? versionElement.GetString() : null;
        var permissions = new List<ExtensionPermission>();

        if (addon.TryGetProperty("userPermissions", out var userPermissions))
        {
            CollectPermissions(userPermissions, "permissions", permissions);
            CollectPermissions(userPermissions, "origins", permissions);
        }

        var path = addon.TryGetProperty("path", out var pathElement) ? pathElement.GetString() : null;

        return new BrowserExtension(
            Id: id!,
            Name: string.IsNullOrWhiteSpace(name) ? id! : name,
            Version: version,
            Browser: "Firefox",
            ProfileName: profileName,
            Permissions: permissions.DistinctBy(p => p.Raw).ToList(),
            SizeBytes: 0,
            Path: path ?? string.Empty,
            LastUpdatedUtc: null);
    }

    private static string[] SafeGetDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return [];
        }
    }

    private static long MeasureDirectory(string path, CancellationToken ct)
    {
        try
        {
            long total = 0;

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Отдельный файл мог исчезнуть.
                }
            }

            return total;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
        {
            return 0;
        }
    }
}
