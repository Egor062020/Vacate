using System.Windows;

namespace Vacate.App.Views;

/// <summary>
/// Раскладка прямоугольников по площади, пропорциональной величине.
/// </summary>
/// <remarks>
/// Алгоритм «квадратизации»: элементы укладываются полосами, и следующий элемент
/// добавляется в полосу до тех пор, пока это улучшает её пропорции. Простое деление
/// пополам давало бы длинные узкие ленты, площадь которых глаз не сравнивает —
/// а вся польза карты в том, чтобы сравнивать площади с одного взгляда.
///
/// Реализация своя, а не из библиотеки: алгоритм занимает полсотни строк, а зависимость
/// пришлось бы тащить в поставку и обновлять вместе с ней.
/// </remarks>
internal static class TreemapLayout
{
    /// <summary>Разложить величины по прямоугольнику.</summary>
    /// <param name="values">Величины в порядке убывания.</param>
    /// <param name="area">Куда укладывать.</param>
    public static IReadOnlyList<Rect> Arrange(IReadOnlyList<long> values, Rect area)
    {
        ArgumentNullException.ThrowIfNull(values);

        var result = new Rect[values.Count];

        if (values.Count == 0 || area.Width <= 0 || area.Height <= 0)
        {
            return result;
        }

        var total = values.Sum();

        if (total <= 0)
        {
            return result;
        }

        // Работаем в единицах площади: величина переводится в площадь один раз,
        // и дальше алгоритм не знает ни про байты, ни про пиксели.
        var scale = area.Width * area.Height / total;
        var areas = values.Select(v => v * scale).ToArray();

        var remaining = area;
        var index = 0;

        while (index < areas.Length)
        {
            var shortSide = Math.Min(remaining.Width, remaining.Height);

            if (shortSide <= 0)
            {
                break;
            }

            // Набираем полосу, пока пропорции элементов в ней улучшаются.
            var count = 1;
            var stripArea = areas[index];
            var worst = Worst(areas, index, count, stripArea, shortSide);

            while (index + count < areas.Length)
            {
                var nextArea = stripArea + areas[index + count];
                var nextWorst = Worst(areas, index, count + 1, nextArea, shortSide);

                if (nextWorst > worst)
                {
                    break;
                }

                stripArea = nextArea;
                worst = nextWorst;
                count++;
            }

            remaining = PlaceStrip(areas, index, count, stripArea, remaining, result);
            index += count;
        }

        return result;
    }

    /// <summary>Худшее (наибольшее) отношение сторон в полосе.</summary>
    private static double Worst(double[] areas, int start, int count, double stripArea, double shortSide)
    {
        if (stripArea <= 0)
        {
            return double.MaxValue;
        }

        var thickness = stripArea / shortSide;
        var worst = 0.0;

        for (var i = start; i < start + count; i++)
        {
            var length = areas[i] / thickness;

            if (length <= 0)
            {
                continue;
            }

            worst = Math.Max(worst, Math.Max(thickness / length, length / thickness));
        }

        return worst == 0 ? double.MaxValue : worst;
    }

    /// <summary>Уложить полосу и вернуть остаток площади.</summary>
    private static Rect PlaceStrip(
        double[] areas, int start, int count, double stripArea, Rect remaining, Rect[] result)
    {
        var horizontal = remaining.Width >= remaining.Height;
        var thickness = horizontal ? stripArea / remaining.Height : stripArea / remaining.Width;

        var offset = 0.0;

        for (var i = start; i < start + count; i++)
        {
            var length = stripArea <= 0
                ? 0
                : areas[i] / stripArea * (horizontal ? remaining.Height : remaining.Width);

            result[i] = horizontal
                ? new Rect(remaining.X, remaining.Y + offset, thickness, length)
                : new Rect(remaining.X + offset, remaining.Y, length, thickness);

            offset += length;
        }

        return horizontal
            ? new Rect(remaining.X + thickness, remaining.Y, Math.Max(0, remaining.Width - thickness), remaining.Height)
            : new Rect(remaining.X, remaining.Y + thickness, remaining.Width, Math.Max(0, remaining.Height - thickness));
    }
}
