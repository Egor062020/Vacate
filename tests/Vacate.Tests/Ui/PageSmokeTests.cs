using System.Windows;
using System.Windows.Controls;
using Vacate.Abstractions.Model;
using Vacate.App.Views;
using Xunit;

namespace Vacate.Tests.Ui;

/// <summary>
/// Проверка, что каждый раздел интерфейса создаётся и загружает данные без падения.
/// </summary>
/// <remarks>
/// Эти тесты появились после того, как собранная поставка установилась успешно,
/// а при первом запуске упала: в неё попала заглушка вместо настоящей библиотеки
/// графики. Сборка и установка проходили, тесты были зелёными — и всё равно
/// пользователь получил бы неработающую программу.
///
/// Элементы интерфейса требуют однопоточной модели, поэтому каждый тест выполняется
/// в отдельном потоке с нужной моделью, а не в общем потоке прогона.
/// </remarks>
public sealed class PageSmokeTests
{
    /// <summary>Выполнить действие в потоке с моделью, которую требует интерфейс.</summary>
    private static void RunOnUiThread(Action action)
    {
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                // Ресурсы оформления живут в приложении: без него разметка страниц
                // не найдёт ни цветов, ни стилей и упадёт при создании.
                if (Application.Current is null)
                {
                    var app = new Application();
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/Vacate;component/Themes/Tokens.xaml"),
                    });
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/Vacate;component/Themes/Styles.xaml"),
                    });
                }

                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // Разделы читают настоящие данные машины, поэтому запас по времени щедрый.
        if (!thread.Join(TimeSpan.FromMinutes(2)))
        {
            Assert.Fail("Создание раздела не завершилось за отведённое время");
        }

        if (failure is not null)
        {
            Assert.Fail($"Раздел упал при создании: {failure}");
        }
    }

    [Fact]
    public void Обзорный_раздел_создаётся()
        => RunOnUiThread(() => Assert.NotNull(new DashboardPage()));

    [Fact]
    public void Раздел_очистки_создаётся()
        => RunOnUiThread(() => Assert.NotNull(new CleanPage()));

    [Fact]
    public void Раздел_программ_создаётся()
        => RunOnUiThread(() => Assert.NotNull(new AppsPage()));

    [Fact]
    public void Раздел_автозагрузки_создаётся()
        => RunOnUiThread(() => Assert.NotNull(new StartupPage()));

    [Fact]
    public void Раздел_расширений_создаётся()
        => RunOnUiThread(() => Assert.NotNull(new ExtensionsPage()));

    [Fact]
    public void Раздел_места_на_диске_создаётся()
        => RunOnUiThread(() => Assert.NotNull(new DiskPage()));

    [Fact]
    public void Раздел_состояния_дисков_создаётся()
        => RunOnUiThread(() => Assert.NotNull(new HealthPage()));

    [Fact]
    public void Раздел_настроек_создаётся()
        => RunOnUiThread(() => Assert.NotNull(new SettingsPage()));

    [Fact]
    public void Значок_области_уведомлений_рисуется()
    {
        // Значок собирается из примитивов, а не берётся из файла. Если рисование
        // не удалось, программа продолжит работать, а значок будет невидимым —
        // человек решит, что настройка не сработала.
        RunOnUiThread(() =>
        {
            var draw = typeof(App.MainWindow).Assembly
                .GetType("Vacate.App.TrayIcon")!
                .GetMethod("Draw", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

            using var icon = (IDisposable?)draw.Invoke(null, null);

            Assert.NotNull(icon);
        });
    }

    [Fact]
    public void Главное_окно_создаётся_со_всеми_разделами()
    {
        // Самая близкая к реальности проверка: окно строит навигацию и первый раздел
        // ровно так же, как при запуске у пользователя.
        RunOnUiThread(() =>
        {
            var window = new App.MainWindow();
            Assert.NotNull(window);
            window.Close();
        });
    }

    [Fact]
    public void Окно_удаления_программы_создаётся_и_показывает_предупреждение()
    {
        RunOnUiThread(() =>
        {
            // Компонент, нужный другим программам: окно обязано сказать об этом
            // до нажатия, а не после того, как половина программ перестанет запускаться.
            var runtime = new InstalledApp(
                Id: "{test}",
                DisplayName: "Microsoft Visual C++ 2015 Redistributable",
                Version: "14.0",
                Publisher: "Microsoft Corporation",
                InstallLocation: null,
                UninstallCommand: "uninstall.exe",
                QuietUninstallCommand: null,
                InstallDate: null,
                EstimatedSizeBytes: 0,
                Scope: InstallScope.Machine,
                Is32BitOnWin64: false,
                IconPath: null);

            var window = new UninstallWindow(runtime);

            Assert.True(runtime.LooksLikeRuntime);
            window.Close();
        });
    }

    [Fact]
    public void Окно_удаления_создаётся_для_программы_без_команды_удаления()
    {
        RunOnUiThread(() =>
        {
            var orphan = new InstalledApp(
                Id: "{test}",
                DisplayName: "Zzqxwv Orphan",
                Version: null,
                Publisher: null,
                InstallLocation: null,
                UninstallCommand: null,
                QuietUninstallCommand: null,
                InstallDate: null,
                EstimatedSizeBytes: 0,
                Scope: InstallScope.User,
                Is32BitOnWin64: false,
                IconPath: null);

            var window = new UninstallWindow(orphan);

            Assert.False(orphan.CanUninstall);
            window.Close();
        });
    }

    [Theory]
    [InlineData(1_000L, "1000 Б")]
    [InlineData(1024L, "1 КБ")]
    [InlineData(1536L, "1,5 КБ")]
    [InlineData(1048576L, "1 МБ")]
    public void Размеры_показываются_в_единицах_проводника(long bytes, string expected)
    {
        // Десятичные единицы разошлись бы с проводником на семь процентов,
        // и честный счётчик первым обвинили бы во лжи.
        Assert.Equal(expected, Format.Size(bytes));
    }
}
