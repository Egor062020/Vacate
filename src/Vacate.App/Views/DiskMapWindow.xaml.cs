using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Vacate.Core.Localization;
using Vacate.Platform.Windows.Files;

namespace Vacate.App.Views;

/// <summary>
/// Карта диска: площадь прямоугольника равна занятому месту.
/// </summary>
/// <remarks>
/// Список отвечает на вопрос «что тут самое большое», карта — на вопрос «как это
/// соотносится между собой». Второй человек задаёт себе первым, глядя на заполненный
/// диск, и список отвечает на него плохо: строки «7,7 ГБ» и «1,4 ГБ» выглядят
/// одинаково, хотя различаются в пять раз.
/// </remarks>
public partial class DiskMapWindow : Window
{
    private readonly Stack<string> _history = new();
    private DirectoryMap? _map;
    private string _current;

    public DiskMapWindow(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        InitializeComponent();

        _current = root;

        Loaded += async (_, _) => await LoadAsync(root);
    }

    private async Task LoadAsync(string path)
    {
        BusyText.Visibility = Visibility.Visible;
        MapCanvas.Children.Clear();

        PathText.Text = path;
        StatusText.Text = string.Empty;

        try
        {
            _map = await Task.Run(() => new DirectorySizes().Measure(path, CancellationToken.None));
            _current = path;

            Draw();
        }
        finally
        {
            BusyText.Visibility = Visibility.Collapsed;
            UpButton.IsEnabled = _history.Count > 0;
        }
    }

    private void OnCanvasResized(object sender, SizeChangedEventArgs e) => Draw();

    private void Draw()
    {
        MapCanvas.Children.Clear();

        if (_map is null || MapCanvas.ActualWidth <= 0 || MapCanvas.ActualHeight <= 0)
        {
            return;
        }

        if (_map.Entries.Count == 0)
        {
            MapCanvas.Children.Add(new TextBlock
            {
                Text = Strings.Get("Map.Empty"),
                Style = (Style)FindResource("SecondaryText"),
                Margin = new Thickness(12),
            });

            return;
        }

        // Мелочь не рисуется: прямоугольник в три пикселя нельзя ни разглядеть,
        // ни нажать, а подпись в нём превращается в грязь.
        var visible = _map.Entries.Where(e => e.SizeBytes * 400 > _map.TotalBytes).ToList();

        if (visible.Count == 0)
        {
            visible = [_map.Entries[0]];
        }

        var rectangles = TreemapLayout.Arrange(
            visible.Select(e => e.SizeBytes).ToList(),
            new Rect(0, 0, MapCanvas.ActualWidth, MapCanvas.ActualHeight));

        for (var i = 0; i < visible.Count; i++)
        {
            AddTile(visible[i], rectangles[i], i, visible.Count);
        }

        UpdateStatus(_map.Entries.Count - visible.Count);
    }

    /// <summary>
    /// Собрать строку состояния целиком.
    /// </summary>
    /// <remarks>
    /// Именно целиком, а не дописыванием: перерисовка происходит при каждом изменении
    /// размера окна, и дописанные части накапливались бы одна за другой.
    /// </remarks>
    private void UpdateStatus(int hidden)
    {
        if (_map is null)
        {
            return;
        }

        var text = Format.Text("Map.Total", Format.Size(_map.TotalBytes), _map.Entries.Count);

        if (_map.SkippedCount > 0)
        {
            text += Format.Text("Map.Unreadable", _map.SkippedCount);
        }

        if (hidden > 0)
        {
            // Скрытое должно быть названо: молчаливый пропуск читается как «этого нет».
            text += Format.Text("Map.TooSmall", hidden);
        }

        StatusText.Text = text;
    }

    private void AddTile(DirectoryEntry entry, Rect rect, int index, int count)
    {
        if (rect.Width < 2 || rect.Height < 2)
        {
            return;
        }

        // Оттенок по месту в списке: крупное темнее и заметнее, мелкое бледнее.
        // Разные цвета для разных папок были бы украшением без смысла.
        var shade = 0.30 + 0.5 * index / Math.Max(1, count - 1);

        var container = new Border
        {
            Width = rect.Width - 2,
            Height = rect.Height - 2,
            Background = new SolidColorBrush(Color.FromRgb(
                (byte)(0xE6 * (1 - shade) + 0x23 * shade),
                (byte)(0x33 * (1 - shade) + 0x25 * shade),
                (byte)(0x29 * (1 - shade) + 0x29 * shade))),
            BorderBrush = (Brush)FindResource("SurfaceBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Cursor = entry.IsDirectory ? Cursors.Hand : Cursors.Arrow,
            ToolTip = $"{entry.Path}\n{Format.Size(entry.SizeBytes)}",
            Tag = entry,
        };

        // Подпись помещается не всегда: в узкую плитку она не влезет,
        // а обрезанное слово хуже, чем его отсутствие.
        if (rect.Width > 90 && rect.Height > 34)
        {
            container.Child = new StackPanel
            {
                Margin = new Thickness(8, 6, 6, 6),
                Children =
                {
                    new TextBlock
                    {
                        Text = entry.Name,
                        Foreground = (Brush)FindResource("TextPrimaryBrush"),
                        FontFamily = (FontFamily)FindResource("UiFont"),
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                    },
                    new TextBlock
                    {
                        Text = Format.Size(entry.SizeBytes),
                        Foreground = (Brush)FindResource("TextPrimaryBrush"),
                        FontFamily = (FontFamily)FindResource("MonoFont"),
                        FontSize = 11,
                        Opacity = 0.85,
                    },
                },
            };
        }

        container.MouseLeftButtonUp += OnTileClick;

        Canvas.SetLeft(container, rect.X + 1);
        Canvas.SetTop(container, rect.Y + 1);

        MapCanvas.Children.Add(container);
    }

    private async void OnTileClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: DirectoryEntry entry })
        {
            return;
        }

        if (!entry.IsDirectory)
        {
            // Сводка по отдельным файлам: открывать внутри нечего, показываем папку.
            OpenInExplorer(entry.Path);
            return;
        }

        _history.Push(_current);

        await LoadAsync(entry.Path);
    }

    private async void OnUp(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0)
        {
            return;
        }

        await LoadAsync(_history.Pop());
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            // Проводник не открылся. Настаивать незачем.
        }
    }
}
