using Vacate.Abstractions.Model;

namespace Vacate.Platform.Windows.Files;

/// <summary>
/// Перевод разрешений расширений на человеческий язык.
/// </summary>
/// <remarks>
/// Ради этого раздел и существует. Список установленных расширений человек видит
/// и в самом браузере; чего он там не видит — что одно из них читает содержимое
/// всех открываемых страниц, включая банк и почту, а другое просто меняет цвет кнопки.
///
/// Формулировки намеренно бытовые: «читает и меняет данные на всех сайтах»
/// понятно каждому, «tabs» и «webRequest» — почти никому.
/// </remarks>
public static class ExtensionPermissions
{
    private static readonly Dictionary<string, (string Description, PermissionLevel Level)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tabs"] = ("Видит адреса и заголовки всех открытых вкладок", PermissionLevel.Notable),
        ["activeTab"] = ("Работает с текущей вкладкой, когда вы сами его вызываете", PermissionLevel.Harmless),
        ["storage"] = ("Хранит свои настройки", PermissionLevel.Harmless),
        ["unlimitedStorage"] = ("Хранит неограниченный объём данных на диске", PermissionLevel.Harmless),
        ["cookies"] = ("Читает и меняет cookies, в том числе данные о входе на сайты", PermissionLevel.SomeSites),
        ["history"] = ("Читает всю историю посещений", PermissionLevel.SomeSites),
        ["bookmarks"] = ("Читает и меняет закладки", PermissionLevel.Notable),
        ["downloads"] = ("Управляет загрузками файлов", PermissionLevel.Notable),
        ["management"] = ("Управляет другими расширениями", PermissionLevel.SomeSites),
        ["nativeMessaging"] = ("Обменивается данными с программами на компьютере", PermissionLevel.SomeSites),
        ["webRequest"] = ("Видит весь сетевой обмен браузера", PermissionLevel.AllSites),
        ["webRequestBlocking"] = ("Может изменять и блокировать сетевые запросы", PermissionLevel.AllSites),
        ["proxy"] = ("Управляет тем, через какой сервер идёт ваш трафик", PermissionLevel.AllSites),
        ["debugger"] = ("Имеет полный отладочный доступ к страницам", PermissionLevel.AllSites),
        ["privacy"] = ("Меняет настройки приватности браузера", PermissionLevel.SomeSites),
        ["clipboardRead"] = ("Читает буфер обмена", PermissionLevel.SomeSites),
        ["clipboardWrite"] = ("Пишет в буфер обмена", PermissionLevel.Harmless),
        ["geolocation"] = ("Определяет ваше местоположение", PermissionLevel.Notable),
        ["notifications"] = ("Показывает уведомления", PermissionLevel.Harmless),
        ["contextMenus"] = ("Добавляет пункты в контекстное меню", PermissionLevel.Harmless),
        ["scripting"] = ("Выполняет свой код на страницах", PermissionLevel.SomeSites),
        ["identity"] = ("Получает данные вашей учётной записи в браузере", PermissionLevel.SomeSites),
        ["idle"] = ("Знает, когда вы отходите от компьютера", PermissionLevel.Harmless),
        ["alarms"] = ("Выполняет действия по расписанию", PermissionLevel.Harmless),
    };

    /// <summary>Перевести разрешение из манифеста.</summary>
    public static ExtensionPermission Translate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ExtensionPermission(raw, "Неизвестное разрешение", PermissionLevel.Notable);
        }

        if (Known.TryGetValue(raw, out var known))
        {
            return new ExtensionPermission(raw, known.Description, known.Level);
        }

        // Шаблоны адресов: именно они дают доступ к содержимому страниц.
        if (raw.Contains("://", StringComparison.Ordinal) || raw.StartsWith('*'))
        {
            var isEverywhere = raw is "<all_urls>" or "*://*/*"
                || raw.StartsWith("*://*.", StringComparison.Ordinal)
                || raw.Contains("://*/*", StringComparison.Ordinal);

            return isEverywhere
                ? new ExtensionPermission(raw, "Читает и меняет данные на ВСЕХ сайтах, которые вы открываете", PermissionLevel.AllSites)
                : new ExtensionPermission(raw, $"Читает и меняет данные на сайтах: {raw}", PermissionLevel.SomeSites);
        }

        if (string.Equals(raw, "<all_urls>", StringComparison.OrdinalIgnoreCase))
        {
            return new ExtensionPermission(raw, "Читает и меняет данные на ВСЕХ сайтах", PermissionLevel.AllSites);
        }

        // Незнакомое разрешение не выдаём за безобидное: мы просто не знаем, что это.
        return new ExtensionPermission(raw, $"Разрешение «{raw}» — назначение неизвестно", PermissionLevel.Notable);
    }

    /// <summary>Короткое описание уровня для показа.</summary>
    public static string DescribeLevel(PermissionLevel level) => level switch
    {
        PermissionLevel.AllSites => "читает все сайты",
        PermissionLevel.SomeSites => "доступ к данным",
        PermissionLevel.Notable => "ограниченный доступ",
        _ => "безобидно",
    };
}
