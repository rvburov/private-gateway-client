using Avalonia;
using Avalonia.Controls;
using PrivateGateway.Client.Infrastructure.Notifications;
using PrivateGateway.Client.Infrastructure.Startup;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.UI;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Services.StartupTiming.Mark("entry");
        // --check-notifications — проверка пакета macOS (CI): центр уведомлений отвечает программе из пакета .app.
        if (args.Contains("--check-notifications"))
        {
            return CheckNotifications();
        }

        if (args.Contains("--check-tray"))
        {
            return CheckTray();
        }

        var paths = AppPaths.ForCurrentUser();
        using var instance = SingleInstance.TryAcquire(paths);
        if (instance is null)
        {
            // Программа уже работает — показать ее окно, а вторую копию не запускать.
            SingleInstance.RequestActivation(paths);
            return 0;
        }

        // --demo — показать программу без службы: подключение — имитация, пока своих серверов нет — демо-серверы.
        App.Startup = new AppStartup(paths, instance, args.Contains(OsAutostart.MinimizedArgument), args.Contains("--demo"));
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>0 — центр уведомлений ответил (и что с разрешением), 1 — уведомлений нет (не macOS или не из пакета), 2 — молчит.</summary>
    private static int CheckNotifications()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.WriteLine("Уведомления macOS: не macOS");
            return 1;
        }

        try
        {
            var status = MacNotifier.CheckAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Console.WriteLine("Уведомления macOS: " + (status ?? "программа не из пакета .app"));
            return status is null ? 1 : 0;
        }
        catch (TimeoutException)
        {
            Console.WriteLine("Уведомления macOS: центр уведомлений не ответил");
            return 2;
        }
    }

    private static int CheckTray()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return 1;
        }

        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "pg-tray-" + Guid.NewGuid().ToString("N")));
        App.Startup = new AppStartup(paths, null, Minimized: false, Demo: true, CheckTray: true);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
    }

    // Используется и дизайнером Avalonia, и тестами без экрана.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
