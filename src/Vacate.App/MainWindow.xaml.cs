using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Vacate.Core.Localization;
using Vacate.App.Views;
using Vacate.Platform.Windows.Files;

namespace Vacate.App;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _pages = [];
    private int _currentIndex;
    private string? _updateUrl;
    private Version? _updateVersion;
    private TrayIcon? _tray;

    public MainWindow()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;

        VersionLabel.Text = version is null
            ? string.Empty
            : $"{Strings.Get("Common.Version")} {version.Major}.{version.Minor}.{version.Build}";

        Navigate("dashboard", animate: false);

        SettingsPage.TrayPreferenceChanged += SetTrayIcon;

        Loaded += async (_, _) =>
        {
            var settings = AppSettings.Load();

            SetTrayIcon(settings.ShowTrayIcon);

            // Знакомство идёт до всего остального: оно объясняет, что программа
            // делает с чужими файлами, и это надо знать до первого нажатия.
            if (!settings.TourShown)
            {
                new FirstRunTour { Owner = this }.ShowDialog();

                (AppSettings.Load() with { TourShown = true }).Save();
            }

            await CheckForUpdatesAsync();
        };

        Closing += OnClosing;
        Closed += (_, _) =>
        {
            SettingsPage.TrayPreferenceChanged -= SetTrayIcon;

            // Значок, не убранный явно, висит в области уведомлений
            // до тех пор, пока по нему не проведут мышью.
            _tray?.Dispose();
            _tray = null;
        };
    }

    private void SetTrayIcon(bool show)
    {
        if (show && _tray is null)
        {
            _tray = new TrayIcon(this);
        }
        else if (!show && _tray is not null)
        {
            _tray.Dispose();
            _tray = null;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Прятать программу вместо выхода можно только когда значок есть
        // и человек сам этого попросил: иначе окно, которое не закрывается,
        // выглядит как поломка.
        var settings = AppSettings.Load();

        if (settings is { ShowTrayIcon: true, MinimizeToTray: true } && _tray is not null)
        {
            e.Cancel = true;
            Hide();
        }
    }

    /// <summary>
    /// Тихо спросить, нет ли новой версии.
    /// </summary>
    /// <remarks>
    /// Единственное обращение программы в сеть. Оно выполняется не чаще раза в сутки
    /// и полностью отключается — это обещано в политике подписи, и обещание должно
    /// иметь способ быть исполненным.
    ///
    /// Сбой проверки не показывается никак: отсутствие сети — обычное состояние,
    /// а не событие, ради которого человека стоит отвлекать.
    /// </remarks>
    private async Task CheckForUpdatesAsync()
    {
        var settings = AppSettings.Load();

        if (!settings.ShouldCheckNow(DateTime.UtcNow))
        {
            return;
        }

        var current = Assembly.GetExecutingAssembly().GetName().Version;

        if (current is null)
        {
            return;
        }

        var result = await new UpdateChecker().CheckAsync(current);

        settings = settings with { LastUpdateCheckUtc = DateTime.UtcNow };
        settings.Save();

        if (result.Status != UpdateStatus.UpdateAvailable || result.LatestVersion is null)
        {
            return;
        }

        // Версия, о которой уже просили не напоминать, больше не всплывает.
        if (string.Equals(settings.DismissedVersion, result.LatestVersion.ToString(), StringComparison.Ordinal))
        {
            return;
        }

        _updateUrl = result.DownloadUrl;
        _updateVersion = result.LatestVersion;

        UpdateText.Text = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Strings.Get("Update.Available"),
            result.LatestVersion);

        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void OnOpenUpdatePage(object sender, RoutedEventArgs e)
    {
        if (_updateUrl is null)
        {
            return;
        }

        try
        {
            // Открываем страницу выпуска в браузере, а не качаем файл сами:
            // без проверки подписи скачивать и запускать чужой код нельзя,
            // а человек в браузере видит, что именно берёт.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://github.com/Egor062020/Vacate/releases/latest",
                UseShellExecute = true,
            });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Браузера нет или он не открылся. Настаивать не будем.
        }

        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private void OnDismissUpdate(object sender, RoutedEventArgs e)
    {
        if (_updateVersion is not null)
        {
            var settings = AppSettings.Load() with { DismissedVersion = _updateVersion.ToString() };
            settings.Save();
        }

        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    private void OnDisableUpdates(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Load() with { CheckForUpdates = false };
        settings.Save();

        UpdateBanner.Visibility = Visibility.Collapsed;

        MessageBox.Show(
            Strings.Get("Settings.UpdatesOffBody"),
            Strings.Get("Settings.UpdatesOffTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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
        "health" => 6,
        _ => 7,
    };

    private static UserControl CreatePage(string key) => key switch
    {
        "clean" => new CleanPage(),
        "apps" => new AppsPage(),
        "startup" => new StartupPage(),
        "extensions" => new ExtensionsPage(),
        "disk" => new DiskPage(),
        "health" => new HealthPage(),
        "settings" => new SettingsPage(),
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
