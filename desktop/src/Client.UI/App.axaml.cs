using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application;
using PrivateGateway.Client.Infrastructure;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.UI;

public partial class App : Avalonia.Application
{
    /// <summary>Контейнер зависимостей: сценарии, реализации и модели представлений.</summary>
    public IServiceProvider Services { get; private set; } = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Services = BuildServices();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = Services.GetRequiredService<ShellViewModel>() };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Тот же состав, что у программы; тесты без экрана строят окно отсюда.</summary>
    public static ServiceProvider BuildServices(Action<IServiceCollection>? overrides = null)
    {
        var services = new ServiceCollection()
            .AddClientApplication()
            .AddClientInfrastructure();
        services.AddSingleton<ToastViewModel>();
        services.AddSingleton<ProtectionViewModel>();
        services.AddSingleton<IClipboardService, AvaloniaClipboardService>();
        services.AddSingleton<Func<INavigator, MainPageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<MainPageViewModel>(sp, navigator));
        services.AddSingleton<ShellViewModel>();
        overrides?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
