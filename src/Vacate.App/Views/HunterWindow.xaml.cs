using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using Vacate.Abstractions.Model;
using Vacate.Platform.Windows.Registry;

namespace Vacate.App.Views;

/// <summary>
/// Режим охотника: определить программу по её окну.
/// </summary>
/// <remarks>
/// Решает задачу, которую иначе не решить: в списке установленного программа называется
/// так, как её назвал издатель, а не так, как написано в её окне. Человек видит окно
/// с рекламой и хочет удалить именно это, а найти в списке не может.
///
/// Приём с перетаскиванием прицела взят не из красоты, а из необходимости: пока кнопка
/// мыши зажата, курсор можно вести над чужими окнами, не переключая на них внимание.
/// Обычное нажатие вывело бы чужое окно вперёд и спрятало наше.
/// </remarks>
public partial class HunterWindow : Window
{
    private readonly WindowHunter _hunter = new();
    private InstalledApp? _found;

    public HunterWindow()
    {
        InitializeComponent();
    }

    private void OnHuntStart(object sender, MouseButtonEventArgs e)
    {
        // Захватываем мышь: иначе, уведя курсор за пределы окна, мы перестанем
        // получать события и не узнаем, где человек отпустил кнопку.
        Crosshair.CaptureMouse();

        Crosshair.MouseMove += OnHuntMove;
        Crosshair.MouseLeftButtonUp += OnHuntEnd;

        ResultTitle.Text = "Ведите прицел к нужному окну…";
        ResultDetails.Text = string.Empty;
        ResultNote.Text = string.Empty;
    }

    private void OnHuntMove(object sender, MouseEventArgs e)
    {
        // Показываем на лету, что под курсором: человек должен видеть,
        // на чём остановится, ещё до того, как отпустит кнопку.
        var result = HuntAtCursor();

        ResultTitle.Text = result.App?.DisplayName
                           ?? result.WindowTitle
                           ?? "Наведите прицел на окно";
    }

    private void OnHuntEnd(object sender, MouseButtonEventArgs e)
    {
        Crosshair.ReleaseMouseCapture();
        Crosshair.MouseMove -= OnHuntMove;
        Crosshair.MouseLeftButtonUp -= OnHuntEnd;

        Show(HuntAtCursor());
    }

    private HuntResult HuntAtCursor()
    {
        GetCursorPos(out var point);

        return _hunter.HuntAt(point.X, point.Y);
    }

    private void Show(HuntResult result)
    {
        _found = result.App;

        ResultTitle.Text = result.App?.DisplayName ?? "Программа не определена";

        var details = new List<string>();

        if (result.WindowTitle is not null)
        {
            details.Add($"Заголовок окна: {result.WindowTitle}");
        }

        if (result.ExecutablePath is not null)
        {
            details.Add($"Запущено из:    {result.ExecutablePath}");
        }

        if (result.App is not null)
        {
            details.Add($"Издатель:       {result.App.Publisher ?? "не указан"}");
            details.Add($"Версия:         {result.App.Version ?? "не указана"}");
        }

        ResultDetails.Text = string.Join(Environment.NewLine, details);

        // Оговорка показывается как есть: догадка по издателю должна остаться
        // догадкой в глазах человека, а не выглядеть установленным фактом.
        ResultNote.Text = result.Note ?? string.Empty;

        UninstallButton.IsEnabled = result.App is not null;
    }

    private void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (_found is null)
        {
            return;
        }

        // Своё окно на время убираем: оно держится поверх остальных,
        // и разговор об удалении шёл бы из-под него.
        Topmost = false;

        var dialog = new UninstallWindow(_found) { Owner = this };
        dialog.ShowDialog();

        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT point);
}
