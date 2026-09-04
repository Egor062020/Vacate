using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Vacate.App.Views;

namespace Vacate.App;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _pages = [];
    private int _currentIndex;

    public MainWindow()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionLabel.Text = version is null ? string.Empty : $"версия {version.Major}.{version.Minor}.{version.Build}";

        Navigate("dashboard", animate: false);
    }

    private void OnNavigate(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || sender is not RadioButton { Tag: string key })
        {
            return;
        }

        Navigate(key, animate: true);
    }

    private void Navigate(string key, bool animate)
    {
        if (!_pages.TryGetValue(key, out var page))
        {
            page = CreatePage(key);
            _pages[key] = page;
        }

        var index = IndexOf(key);

        // Направление сдвига зависит от того, куда движемся по списку разделов:
        // переход вниз выглядит как движение вниз. Ненаправленное растворение
        // читается хуже — глазу не за что зацепиться.
        var direction = index >= _currentIndex ? 1 : -1;
        _currentIndex = index;

        PageHost.Content = page;

        if (!animate || SystemParameters.ClientAreaAnimation == false)
        {
            // Уважаем системную настройку уменьшения анимации.
            PageHost.Opacity = 1;
            return;
        }

        var transform = (TranslateTransform)PageHost.RenderTransform;
        var duration = TimeSpan.FromMilliseconds(220);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            From = 14 * direction,
            To = 0,
            Duration = duration,
            EasingFunction = ease,
        });

        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = duration,
            EasingFunction = ease,
        });
    }

    private static int IndexOf(string key) => key switch
    {
        "dashboard" => 0,
        "clean" => 1,
        "apps" => 2,
        "startup" => 3,
        "extensions" => 4,
        "disk" => 5,
        _ => 6,
    };

    private static UserControl CreatePage(string key) => key switch
    {
        "clean" => new CleanPage(),
        "apps" => new AppsPage(),
        "startup" => new StartupPage(),
        "extensions" => new ExtensionsPage(),
        "disk" => new DiskPage(),
        "health" => new HealthPage(),
        _ => new DashboardPage(),
    };

    private void OnTitleBarDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            OnMaximize(sender, e);
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
