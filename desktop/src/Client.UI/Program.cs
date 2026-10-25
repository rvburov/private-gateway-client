using Avalonia;
using Avalonia.Controls;
using PrivateGateway.Client.Infrastructure.Startup;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.UI;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var paths = AppPaths.ForCurrentUser();
        using var instance = SingleInstance.TryAcquire(paths);
        if (instance is null)
        {
            // Программа уже работает — показать ее окно, а вторую копию не запускать.
            SingleInstance.RequestActivation(paths);
            return;
        }

        // --demo — показать программу без службы: подключение — имитация, пока своих серверов нет — демо-серверы.
        App.Startup = new AppStartup(paths, instance, args.Contains(OsAutostart.MinimizedArgument), args.Contains("--demo"));
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    // Используется и дизайнером Avalonia, и тестами без экрана.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
