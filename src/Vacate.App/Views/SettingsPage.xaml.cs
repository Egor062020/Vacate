using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Vacate.Core.Localization;
using Vacate.Platform.Windows.Files;
using Vacate.Platform.Windows.Registry;

namespace Vacate.App.Views;

/// <summary>
/// Настройки: всё, что программа делает без участия человека.
/// </summary>
/// <remarks>
/// Раздел появился не ради полноты, а по необходимости. Политика подписи обещала,
/// что проверку обновлений можно выключить, автоматическая очистка настраивалась только
/// командой в консоли, а значка в области уведомлений не было вовсе — при том, что
/// и то, и другое, и третье было обещано.
/// </remarks>
public partial class SettingsPage : UserControl
{
    /// <summary>Сообщает окну, что значок нужно показать или убрать.</summary>
    public static event Action<bool>? TrayPreferenceChanged;

    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();

        Loaded += (_, _) => Load();
    }

    private void Load()
    {
        // Пока раскладываем сохранённое по элементам, их обработчики
        // не должны принимать это за выбор человека.
        _loading = true;

        try
        {
            var settings = AppSettings.Load();

            LanguageEnglish.IsChecked = Strings.IsEnglish;
            LanguageRussian.IsChecked = !Strings.IsEnglish;

            UpdatesEnabled.IsChecked = settings.CheckForUpdates;
            TrayEnabled.IsChecked = settings.ShowTrayIcon;
            MinimizeToTray.IsChecked = settings.MinimizeToTray;
            MinimizeToTray.IsEnabled = settings.ShowTrayIcon;

            var state = new ScheduleManager().GetState();

            if (!state.Enabled)
            {
                ScheduleOff.IsChecked = true;
                ScheduleStatus.Text = Strings.Get("Settings.ScheduleOffNow");
            }
            else
            {
                // Частота приходит значением, а не текстом. Раньше здесь искалось
                // слово «месяц» в человеческом описании — при переводе интерфейса
                // такая проверка перестала бы работать молча.
                var monthly = state.Frequency == ScheduleFrequency.Monthly;

                ScheduleMonthly.IsChecked = monthly;
                ScheduleWeekly.IsChecked = !monthly;

                ScheduleStatus.Text = state.NextRun is { } next
                    ? $"{Strings.Get("Settings.ScheduleOnNow")} {Strings.Get("Settings.NextRun")} {next:dd.MM.yyyy HH:mm}"
                    : Strings.Get("Settings.ScheduleOnNow");
            }
        }
        finally
        {
            _loading = false;
        }
    }

    private void OnApplySchedule(object sender, RoutedEventArgs e)
    {
        var manager = new ScheduleManager();

        if (ScheduleOff.IsChecked == true)
        {
            var off = manager.Disable();
            ScheduleStatus.Text = off.Message;

            return;
        }

        var executor = Path.Combine(AppContext.BaseDirectory, "vacate-cli.exe");

        if (!File.Exists(executor))
        {
            ScheduleStatus.Text = Strings.Get("Settings.NoCli");
            return;
        }

        var frequency = ScheduleMonthly.IsChecked == true ? ScheduleFrequency.Monthly : ScheduleFrequency.Weekly;
        var result = manager.Enable(executor, frequency, ScheduleAtLogon.IsChecked == true);

        ScheduleStatus.Text = result.Message;

        if (result.Success)
        {
            // Показываем время следующего запуска: обещание «раз в неделю»
            // без даты проверить нельзя.
            var state = manager.GetState();

            if (state.NextRun is { } next)
            {
                ScheduleStatus.Text += $" {Strings.Get("Settings.NextRun")} {next:dd.MM.yyyy HH:mm}";
            }
        }
    }

    private void OnTraySettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var show = TrayEnabled.IsChecked == true;

        var settings = AppSettings.Load() with
        {
            ShowTrayIcon = show,

            // Прятать программу некуда, если значка нет: настройка без значка
            // означала бы окно, которое нельзя закрыть.
            MinimizeToTray = show && MinimizeToTray.IsChecked == true,
        };

        settings.Save();

        MinimizeToTray.IsEnabled = show;

        if (!show)
        {
            MinimizeToTray.IsChecked = false;
        }

        TrayPreferenceChanged?.Invoke(show);
    }

    /// <summary>
    /// Запомнить выбранный язык.
    /// </summary>
    /// <remarks>
    /// Применяется при следующем запуске: разметка берёт тексты при построении окна,
    /// и живая смена языка потребовала бы уведомлений на каждую надпись ради
    /// возможности, которой пользуются раз в жизни. О перезапуске сказано рядом.
    /// </remarks>
    private void OnLanguageChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        (AppSettings.Load() with { Language = LanguageEnglish.IsChecked == true ? "en" : "ru" }).Save();
    }

    private void OnUpdateSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        (AppSettings.Load() with { CheckForUpdates = UpdatesEnabled.IsChecked == true }).Save();
    }

    private void OnOpenQuarantine(object sender, RoutedEventArgs e)
    {
        var stores = FileSystemQuarantine.EnumerateStores().Where(Directory.Exists).ToList();

        if (stores.Count == 0)
        {
            MessageBox.Show(Strings.Get("Settings.QuarantineEmpty"), Strings.Get("Settings.Rollback"),
                MessageBoxButton.OK, MessageBoxImage.Information);

            return;
        }

        OpenFolder(stores[0]);
    }

    private void OnOpenBackups(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(RegistryBackup.Directory))
        {
            MessageBox.Show(Strings.Get("Settings.NoBackups"), Strings.Get("Settings.OpenBackups"),
                MessageBoxButton.OK, MessageBoxImage.Information);

            return;
        }

        OpenFolder(RegistryBackup.Directory);
    }

    private void OnCreateRestorePoint(object sender, RoutedEventArgs e)
    {
        var executor = Path.Combine(AppContext.BaseDirectory, "vacate-cli.exe");

        if (!File.Exists(executor))
        {
            return;
        }

        try
        {
            // Точку может создать только процесс с правами администратора,
            // поэтому запрашиваем их штатным окном системы.
            Process.Start(new ProcessStartInfo
            {
                FileName = executor,
                Arguments = "restore-point",
                UseShellExecute = true,
                Verb = "runas",
            });
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Отказ в правах — решение человека.
        }
    }

    private void OnShowTour(object sender, RoutedEventArgs e)
    {
        // Знакомство показывается один раз, но вернуться к нему человек вправе:
        // половину прочитанного при первом запуске он к этому моменту забыл.
        new FirstRunTour { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Проводник не открылся. Настаивать незачем.
        }
    }
}
