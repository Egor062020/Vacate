using System.Windows;

namespace Vacate.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Перехват ошибок подключается до всего остального: сбой при запуске
        // тоже должен объясняться человеку, а не закрывать окно молча.
        CrashHandler.Install(this);

        base.OnStartup(e);
    }
}
