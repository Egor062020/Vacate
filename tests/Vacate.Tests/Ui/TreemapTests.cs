using System.Windows;
using Vacate.App.Views;
using Xunit;

namespace Vacate.Tests.Ui;

/// <summary>
/// Раскладка карты диска.
/// </summary>
/// <remarks>
/// Вся польза карты в том, что площади можно сравнивать с одного взгляда. Значит,
/// проверять надо именно площади: если прямоугольник вдвое большего каталога
/// не вдвое больше, карта не просто некрасива — она врёт.
/// </remarks>
public sealed class TreemapLayoutTests
{
    private static readonly Rect Area = new(0, 0, 800, 600);

    /// <summary>Метод раскладки: он внутренний, потому что нужен только своему окну.</summary>
    private static IReadOnlyList<Rect> Arrange(IReadOnlyList<long> values)
    {
        var method = typeof(DiskMapWindow).Assembly
            .GetType("Vacate.App.Views.TreemapLayout")!
            .GetMethod(
                "Arrange",
                System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Static)!;

        return (IReadOnlyList<Rect>)method.Invoke(null, [values, Area])!;
    }

    [Fact]
    public void Площади_пропорциональны_величинам()
    {
        var rectangles = Arrange([400L, 200L, 100L, 100L]);

        var first = rectangles[0].Width * rectangles[0].Height;
        var second = rectangles[1].Width * rectangles[1].Height;

        // Вдвое больший каталог должен занимать вдвое большую площадь,
        // иначе карта вводит в заблуждение вместо того, чтобы объяснять.
        Assert.InRange(first / second, 1.9, 2.1);
    }

    [Fact]
    public void Вся_площадь_занята_без_остатка()
    {
        var rectangles = Arrange([300L, 250L, 200L, 150L, 100L]);
        var covered = rectangles.Sum(r => r.Width * r.Height);

        Assert.InRange(covered / (Area.Width * Area.Height), 0.98, 1.02);
    }

    [Fact]
    public void Прямоугольники_не_выходят_за_границы()
    {
        var rectangles = Arrange([500L, 300L, 120L, 80L, 40L, 20L]);

        Assert.All(rectangles, r =>
        {
            Assert.True(r.X >= -0.01, $"левый край: {r.X}");
            Assert.True(r.Y >= -0.01, $"верхний край: {r.Y}");
            Assert.True(r.Right <= Area.Width + 0.01, $"правый край: {r.Right}");
            Assert.True(r.Bottom <= Area.Height + 0.01, $"нижний край: {r.Bottom}");
        });
    }

    [Fact]
    public void Прямоугольники_не_накладываются_друг_на_друга()
    {
        var rectangles = Arrange([400L, 300L, 200L, 100L, 50L]);

        for (var i = 0; i < rectangles.Count; i++)
        {
            for (var j = i + 1; j < rectangles.Count; j++)
            {
                var intersection = Rect.Intersect(rectangles[i], rectangles[j]);

                var overlap = intersection.IsEmpty ? 0 : intersection.Width * intersection.Height;

                // Наложение означало бы, что часть места посчитана дважды.
                Assert.True(overlap < 1.0, $"плитки {i} и {j} накладываются на {overlap:F1}");
            }
        }
    }

    [Fact]
    public void Пропорции_плиток_остаются_обозримыми()
    {
        // Смысл квадратизации: длинную узкую ленту глаз не сравнивает с квадратом.
        // Простое деление пополам давало бы отношения в десятки раз.
        var rectangles = Arrange([100L, 100L, 100L, 100L, 100L, 100L]);

        Assert.All(rectangles.Where(r => r is { Width: > 0, Height: > 0 }), r =>
        {
            var ratio = Math.Max(r.Width / r.Height, r.Height / r.Width);

            Assert.True(ratio < 4, $"вытянутая плитка: {r.Width:F0}×{r.Height:F0}");
        });
    }

    [Fact]
    public void Пустой_список_не_роняет_раскладку()
    {
        Assert.Empty(Arrange([]));
    }

    [Fact]
    public void Нулевые_величины_не_роняют_раскладку()
    {
        // Каталог нулевого размера — обычное дело, и деление на ноль здесь
        // уронило бы всю карту.
        var rectangles = Arrange([0L, 0L]);

        Assert.All(rectangles, r => Assert.Equal(0, r.Width * r.Height));
    }
}
