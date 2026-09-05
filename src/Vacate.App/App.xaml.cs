using System.Windows;

namespace Vacate.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Перехват ошибок подключается до всего остального: сбой при запуске
        // тоже должен объясняться человеку, а не закрывать окно молча.
        CrashHandler.Install(this);

        // Язык выбирается до создания окон: разметка берёт тексты при построении.
        Vacate.Core.Localization.Strings.Use(Vacate.Platform.Windows.Files.AppSettings.Load().Language);

        base.OnStartup(e);
    }
}
