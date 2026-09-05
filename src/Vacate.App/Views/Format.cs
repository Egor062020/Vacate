using System.Globalization;
using Vacate.App.Localization;

namespace Vacate.App.Views;

/// <summary>Единое форматирование величин для всего интерфейса.</summary>
/// <remarks>
/// Открыт для тестов: правило про двоичные единицы легко нарушить незаметно,
/// а расхождение с проводником подрывает доверие к «честному счётчику».
/// </remarks>
public static class Format
{
    /// <summary>Переведённый текст с подстановками.</summary>
    public static string Text(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Strings.Get(key), args);

    /// <summary>
    /// Объём в двоичных единицах — как в проводнике Windows.
    /// </summary>
    /// <remarks>
    /// Десятичные единицы разошлись бы с проводником примерно на семь процентов,
    /// и «честный счётчик» первым обвинили бы во лжи, хотя врал бы не он.
    /// </remarks>
    public static string Size(long bytes)
    {
        // Сокращения единиц переводятся: «КБ» в английском интерфейсе выглядит
        // так же чужеродно, как «KB» в русском.
        string[] units = Strings.IsEnglish
            ? ["B", "KB", "MB", "GB", "TB"]
            : ["Б", "КБ", "МБ", "ГБ", "ТБ"];

        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} {units[0]}" : $"{value:0.##} {units[unit]}";
    }

    /// <summary>Сократить длинную строку с многоточием.</summary>
    public static string Trim(string? value, int length)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "—";
        }

        return value.Length <= length ? value : value[..(length - 1)] + "…";
    }
}
