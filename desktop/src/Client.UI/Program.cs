using Avalonia;
using Avalonia.Controls;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.UI;

internal static class Program
{
    /// <summary>Флаг автозапуска: окно не показывать, ждать в трее.</summary>
    public const string MinimizedArgument = "--minimized";

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

        App.Startup = new AppStartup(paths, instance, args.Contains(MinimizedArgument));
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    // Используется и дизайнером Avalonia, и тестами без экрана.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
