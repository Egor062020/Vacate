using System.Text.RegularExpressions;
using Vacate.Core.Localization;
using Xunit;

namespace Vacate.Tests.Ui;

/// <summary>
/// Полнота и согласованность перевода.
/// </summary>
/// <remarks>
/// Пропущенный ключ не ломает программу: вместо английского текста человек увидит
/// русский. Именно поэтому такие пропуски копятся незаметно — заметить их можно
/// только проверкой, а не запуском.
/// </remarks>
public sealed class LocalizationTests
{
    private static IReadOnlyList<string> AllKeys()
    {
        var field = typeof(Strings).GetField(
            "Russian",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        return ((Dictionary<string, string>)field.GetValue(null)!).Keys.ToList();
    }

    private static IReadOnlyList<string> MissingEnglish()
    {
        var method = typeof(Strings).GetMethod(
            "MissingEnglishKeys",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        return ((IEnumerable<string>)method.Invoke(null, null)!).ToList();
    }

    [Fact]
    public void Каждый_текст_переведён_на_английский()
    {
        var missing = MissingEnglish();

        Assert.True(missing.Count == 0, "нет английского перевода: " + string.Join(", ", missing));
    }

    [Fact]
    public void Подстановки_совпадают_в_обоих_языках()
    {
        // Расхождение здесь роняет программу при показе: строка ждёт два значения,
        // а получает одно. Русский текст при этом работает, английский падает —
        // и падает у того, кто не сможет об этом рассказать.
        var mismatched = new List<string>();

        foreach (var key in AllKeys())
        {
            Strings.Use("ru");
            var russian = Placeholders(Strings.Get(key));

            Strings.Use("en");
            var english = Placeholders(Strings.Get(key));

            if (!russian.SetEquals(english))
            {
                mismatched.Add(key);
            }
        }

        Strings.Use("ru");

        Assert.True(mismatched.Count == 0, "подстановки расходятся: " + string.Join(", ", mismatched));
    }

    [Fact]
    public void Английский_текст_не_остался_русским()
    {
        // Скопировать русскую строку в английский словарь и забыть перевести —
        // ошибка, которую проверка полноты не ловит: ключ на месте.
        Strings.Use("en");

        var cyrillic = AllKeys()
            .Where(k => !k.StartsWith("Settings.Language", StringComparison.Ordinal))
            .Where(k => Regex.IsMatch(Strings.Get(k), "[А-Яа-яЁё]"))
            .ToList();

        Strings.Use("ru");

        Assert.True(cyrillic.Count == 0, "остались непереведёнными: " + string.Join(", ", cyrillic));
    }

    [Fact]
    public void Неизвестный_ключ_не_роняет_показ()
    {
        // Лучше показать ключ, чем упасть: пропущенная строка — это неудобство,
        // а исключение при отрисовке — закрывшееся окно.
        Assert.Equal("Нет.Такого.Ключа", Strings.Get("Нет.Такого.Ключа"));
    }

    [Fact]
    public void Язык_системы_выбирается_когда_он_не_задан()
    {
        Strings.Use(null);

        // На этой машине система русская, значит и язык должен быть русским.
        var expected = System.Globalization.CultureInfo.CurrentUICulture
            .TwoLetterISOLanguageName.Equals("ru", StringComparison.OrdinalIgnoreCase);

        Assert.Equal(!expected, Strings.IsEnglish);

        Strings.Use("ru");
    }

    private static HashSet<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{(\d+)")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
}
