using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Половина имён в наборах Windows Forms и WPF совпадает: Pen, Color, Point, Brush.
// Псевдонимы делают явным, о каком из двух идёт речь в каждой строке.
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Vacate.App;

/// <summary>
/// Значок в области уведомлений.
/// </summary>
/// <remarks>
/// Нужен ради того, для чего эту область и придумали: программа, которая должна быть
/// под рукой, но не должна занимать место на панели задач. Отсюда же запускается быстрая
/// очистка — единственное действие, ради которого человек открывает программу чаще всего.
///
/// Значок рисуется тем же построением, что и знак в окне, а не берётся из файла .ico:
/// два изображения одного знака неизбежно разошлись бы, а собранное из примитивов
/// остаётся резким при любом масштабе экрана.
///
/// Область уведомлений живёт по своим правилам: значок, не убранный явно, остаётся
/// висеть после закрытия программы до тех пор, пока по нему не проведут мышью.
/// Поэтому уборка обязательна, а не желательна.
/// </remarks>
internal sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Window _window;
    private Drawing.Icon? _drawn;

    public TrayIcon(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        _window = window;
        _drawn = Draw();

        var menu = new Forms.ContextMenuStrip();

        menu.Items.Add(Vacate.Core.Localization.Strings.Get("Tray.Open"), null, (_, _) => Show());
        menu.Items.Add(Vacate.Core.Localization.Strings.Get("Tray.QuickClean"), null, (_, _) => QuickClean());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Vacate.Core.Localization.Strings.Get("Tray.Exit"), null, (_, _) => Application.Current.Shutdown());

        _icon = new Forms.NotifyIcon
        {
            Icon = _drawn,
            Visible = true,
            Text = Vacate.Core.Localization.Strings.Get("Tray.Tooltip"),
            ContextMenuStrip = menu,
        };

        // Двойной щелчок открывает окно: так ведут себя все программы,
        // живущие в этой области, и нарушать привычку незачем.
        _icon.DoubleClick += (_, _) => Show();
    }

    /// <summary>Показать окно и вывести его вперёд.</summary>
    private void Show()
    {
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    /// <summary>
    /// Быстрая очистка без открытия окна.
    /// </summary>
    /// <remarks>
    /// Выполняется тем же тихим режимом, что и работа по расписанию: только безопасные
    /// категории и без единого вопроса. Показывать окно с вопросами из области уведомлений
    /// значит превратить действие «одно нажатие» в разговор.
    /// </remarks>
    private void QuickClean()
    {
        var executor = Path.Combine(AppContext.BaseDirectory, "vacate-cli.exe");

        if (!File.Exists(executor))
        {
            _icon.ShowBalloonTip(5000, "Vacate",
                Vacate.Core.Localization.Strings.Get("Tray.NoCli"), Forms.ToolTipIcon.Warning);

            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = executor,
                Arguments = "--quiet-clean",
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            _icon.ShowBalloonTip(4000, "Vacate", Vacate.Core.Localization.Strings.Get("Tray.Started"), Forms.ToolTipIcon.Info);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            _icon.ShowBalloonTip(5000, "Vacate", ex.Message, Forms.ToolTipIcon.Error);
        }
    }

    /// <summary>
    /// Нарисовать знак программы значком нужного системе размера.
    /// </summary>
    /// <remarks>
    /// Тот же знак, что в окне: разлом буквы V с отлетающими осколками. Рисуется
    /// в размер, который система запрашивает для этой области, поэтому остаётся
    /// резким и на обычном экране, и на экране с увеличенным масштабом.
    /// </remarks>
    private static Drawing.Icon? Draw()
    {
        try
        {
            var size = Forms.SystemInformation.SmallIconSize.Width switch
            {
                <= 16 => 16,
                <= 24 => 24,
                <= 32 => 32,
                _ => 48,
            };

            var visual = new DrawingVisual();

            using (var context = visual.RenderOpen())
            {
                var scale = size / 28.0;
                var white = new Pen(new SolidColorBrush(Color.FromRgb(0xF2, 0xF3, 0xF5)), 3.6 * scale)
                {
                    StartLineCap = PenLineCap.Square,
                    EndLineCap = PenLineCap.Square,
                };

                var accent = new SolidColorBrush(Color.FromRgb(0xE6, 0x33, 0x29));
                var accentPen = new Pen(accent, 3.6 * scale)
                {
                    StartLineCap = PenLineCap.Square,
                    EndLineCap = PenLineCap.Square,
                };

                context.DrawLine(white, new Point(6 * scale, 5 * scale), new Point(14 * scale, 22 * scale));
                context.DrawLine(accentPen, new Point(22 * scale, 5 * scale), new Point(18.5 * scale, 12 * scale));
                context.DrawRectangle(accent, null, new Rect(15 * scale, 14 * scale, 3.6 * scale, 3.6 * scale));
                context.DrawRectangle(accent, null, new Rect(14 * scale, 19 * scale, 2.6 * scale, 2.6 * scale));
            }

            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));

            using var stream = new MemoryStream();
            encoder.Save(stream);
            stream.Position = 0;

            using var image = new Drawing.Bitmap(stream);

            return Drawing.Icon.FromHandle(image.GetHicon());
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            // Без значка программа работает: область уведомлений — удобство,
            // а не условие. Падать из-за неё нельзя.
            return null;
        }
    }

    public void Dispose()
    {
        // Порядок важен: сначала прячем, потом освобождаем. Иначе значок
        // остаётся висеть в области уведомлений до движения мышью по нему.
        _icon.Visible = false;
        _icon.Dispose();

        _drawn?.Dispose();
        _drawn = null;
    }
}
