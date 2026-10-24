using Avalonia;
using Avalonia.Headless;
using PrivateGateway.Client.Tests.UI;
using PrivateGateway.Client.UI;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace PrivateGateway.Client.Tests.UI;

/// <summary>Приложение для тестов: настоящий App, отрисовка Skia без экрана — снимки как на экране.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
