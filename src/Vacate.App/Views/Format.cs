namespace Vacate.App.Views;

/// <summary>Единое форматирование величин для всего интерфейса.</summary>
internal static class Format
{
    /// <summary>
    /// Объём в двоичных единицах — как в проводнике Windows.
    /// </summary>
    /// <remarks>
    /// Десятичные единицы разошлись бы с проводником примерно на семь процентов,
    /// и «честный счётчик» первым обвинили бы во лжи, хотя врал бы не он.
    /// </remarks>
    public static string Size(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} Б" : $"{value:0.##} {units[unit]}";
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
