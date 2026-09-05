using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Vacate.App;

/// <summary>
/// Перехват необработанных ошибок.
/// </summary>
/// <remarks>
/// Программа раздаётся людям, у которых нет ни отладчика, ни желания разбираться.
/// Без этого обработчика окно просто закрывается: человек не понимает, что произошло,
/// и не может ничего сообщить. Отчёт сохраняется в файл, который можно приложить
/// к сообщению об ошибке.
///
/// Из отчёта убирается имя пользователя: пути вида C:\Users\Иван содержат личные
/// данные, а отчёт человек будет кому-то пересылать.
/// </remarks>
internal static class CrashHandler
{
    private static string ReportDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Vacate", "crash");

    /// <summary>Подключить перехват ко всем источникам ошибок.</summary>
    public static void Install(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);

        application.DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;
        TaskScheduler.UnobservedTaskException += OnTaskException;
    }

    private static void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var path = Save(e.Exception, "интерфейс");
        Show(e.Exception, path);

        // Ошибка обработана: закрывать программу целиком из-за сбоя в одном
        // разделе незачем, остальные продолжат работать.
        e.Handled = true;
    }

    private static void OnDomainException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            var path = Save(exception, "фон");

            // Здесь программу уже не спасти, но сказать человеку, что случилось, обязаны.
            Show(exception, path);
        }
    }

    private static void OnTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Save(e.Exception, "фоновая задача");

        // Не показываем окно: такие ошибки часто возникают при закрытии программы
        // и беспокоить ими человека незачем — записи в файл достаточно.
        e.SetObserved();
    }

    private static string? Save(Exception exception, string source)
    {
        try
        {
            Directory.CreateDirectory(ReportDirectory);

            var path = Path.Combine(ReportDirectory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

            var report = new StringBuilder()
                .AppendLine("Отчёт об ошибке Vacate")
                .AppendLine($"Время:   {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
                .AppendLine($"Версия:  {typeof(CrashHandler).Assembly.GetName().Version}")
                .AppendLine($"Система: {Environment.OSVersion.VersionString}")
                .AppendLine($"Источник: {source}")
                .AppendLine()
                .AppendLine(Sanitize(exception.ToString()))
                .ToString();

            File.WriteAllText(path, report, Encoding.UTF8);

            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Даже записать отчёт не вышло. Окно всё равно покажем.
            return null;
        }
    }

    /// <summary>
    /// Убрать из отчёта личные данные.
    /// </summary>
    /// <remarks>
    /// Пути содержат имя пользователя, а отчёт предназначен для пересылки.
    /// </remarks>
    internal static string Sanitize(string text)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (!string.IsNullOrEmpty(profile))
        {
            text = text.Replace(profile, @"C:\Users\<пользователь>", StringComparison.OrdinalIgnoreCase);
        }

        var userName = Environment.UserName;

        if (!string.IsNullOrEmpty(userName))
        {
            text = text.Replace(userName, "<пользователь>", StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    private static void Show(Exception exception, string? reportPath)
    {
        try
        {
            var message = new StringBuilder()
                .AppendLine("Произошла ошибка, которую программа не смогла обработать.")
                .AppendLine()
                .AppendLine($"Что случилось: {exception.Message}")
                .AppendLine();

            if (reportPath is not null)
            {
                message
                    .AppendLine("Подробности сохранены в файл:")
                    .AppendLine(reportPath)
                    .AppendLine()
                    .AppendLine("Его можно приложить к сообщению об ошибке — личные данные из него убраны.");
            }

            MessageBox.Show(message.ToString(), "Vacate", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // Показать окно не удалось — значит интерфейс уже разрушен. Отчёт записан,
            // и это всё, что мы можем сделать.
        }
    }
}
