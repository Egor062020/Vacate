using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
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

            UpdatesEnabled.IsChecked = settings.CheckForUpdates;
            TrayEnabled.IsChecked = settings.ShowTrayIcon;
            MinimizeToTray.IsChecked = settings.MinimizeToTray;
            MinimizeToTray.IsEnabled = settings.ShowTrayIcon;

            var state = new ScheduleManager().GetState();

            if (!state.Enabled)
            {
                ScheduleOff.IsChecked = true;
                ScheduleStatus.Text = "Сейчас выключена.";
            }
            else
            {
                // Какая именно частота выбрана, планировщик сообщает описанием;
                // еженедельная — то, что предлагается по умолчанию.
                var monthly = state.Frequency?.Contains("месяц", StringComparison.OrdinalIgnoreCase) == true;

                ScheduleMonthly.IsChecked = monthly;
                ScheduleWeekly.IsChecked = !monthly;

                ScheduleStatus.Text = state.NextRun is { } next
                    ? $"Включена. Следующий запуск: {next:dd.MM.yyyy HH:mm}"
                    : "Включена.";
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
            ScheduleStatus.Text = "Рядом с программой нет vacate-cli.exe — поставка неполная.";
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
                ScheduleStatus.Text += $" Следующий запуск: {next:dd.MM.yyyy HH:mm}";
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
            MessageBox.Show("Карантин пуст: возвращать нечего.", "Карантин",
                MessageBoxButton.OK, MessageBoxImage.Information);

            return;
        }

        OpenFolder(stores[0]);
    }

    private void OnOpenBackups(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(RegistryBackup.Directory))
        {
            MessageBox.Show("Копий ветвей реестра ещё не создавалось.", "Копии реестра",
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
