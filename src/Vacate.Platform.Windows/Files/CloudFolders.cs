using Microsoft.Win32;

namespace Vacate.Platform.Windows.Files;

/// <summary>
/// Папки, содержимое которых синхронизируется с облачным хранилищем.
/// </summary>
/// <remarks>
/// Самый дорогой сценарий потери данных в продукте. На большинстве современных установок
/// Рабочий стол, Документы и Изображения перенаправлены в облако, и поиск одинаковых копий
/// работает именно там. Удаление «лишней копии» в такой папке уносит файл со ВСЕХ устройств
/// пользователя — включая телефон, где он о существовании этой программы не знает.
///
/// Карантин здесь бессилен: он вернёт файл на диск, но событие удаления уже разошлось
/// по устройствам, и вернётся ли файл обратно — решает служба синхронизации, а не мы.
/// Поэтому такие файлы помечаются, охрана поднимает им уровень до красного, а человек
/// получает прямое предупреждение вместо обычного подтверждения.
///
/// Определение идёт по нескольким признакам сразу: у одного пользователя корень записан
/// в переменной среды, у другого — только в реестре, у третьего папка перенесена руками
/// на другой диск.
/// </remarks>
public static class CloudFolders
{
    private static readonly Lazy<IReadOnlyList<string>> Roots = new(Discover);

    /// <summary>Известные корни синхронизируемых папок.</summary>
    public static IReadOnlyList<string> KnownRoots => Roots.Value;

    /// <summary>Лежит ли путь внутри синхронизируемой папки.</summary>
    public static bool Contains(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string full;

        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        // Сравнение посегментное: иначе «C:\Users\Имя\OneDriveArchive» считался бы
        // находящимся внутри «C:\Users\Имя\OneDrive».
        return Roots.Value.Any(root =>
            full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> Discover()
    {
        var found = new List<string>();

        // Переменные среды: их выставляет сам клиент синхронизации при входе.
        foreach (var name in (string[])["OneDrive", "OneDriveConsumer", "OneDriveCommercial"])
        {
            Add(found, Environment.GetEnvironmentVariable(name));
        }

        AddFromRegistry(found, @"SOFTWARE\Microsoft\OneDrive\Accounts\Personal", "UserFolder");
        AddFromRegistry(found, @"SOFTWARE\Microsoft\OneDrive\Accounts\Business1", "UserFolder");
        AddFromRegistry(found, @"SOFTWARE\Dropbox\ks", "Path");

        // Клиенты, не оставляющие следа в известных местах, ловятся по имени папки
        // в профиле: так называются каталоги по умолчанию у всех распространённых служб.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrEmpty(profile))
        {
            foreach (var candidate in (string[])
                     ["OneDrive", "Dropbox", "Google Drive", "GoogleDrive", "YandexDisk", "Яндекс.Диск", "iCloudDrive", "Creative Cloud Files"])
            {
                Add(found, Path.Combine(profile, candidate));
            }
        }

        return found;
    }

    private static void AddFromRegistry(List<string> found, string subKey, string valueName)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(subKey);

            Add(found, key?.GetValue(valueName) as string);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            // Ветка недоступна — просто не добавляем этот корень.
        }
    }

    private static void Add(List<string> found, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);

            if (Directory.Exists(full) && !found.Contains(full, StringComparer.OrdinalIgnoreCase))
            {
                found.Add(full);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Негодный путь просто не попадает в список.
        }
    }
}
