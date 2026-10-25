using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Infrastructure;
using PrivateGateway.Client.Infrastructure.Storage;
using PrivateGateway.Client.UI.Localization;
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
        Services = BuildServices(AppPaths.ForCurrentUser());
        Loc.Instance.SetLanguage(Services.GetRequiredService<SettingsService>().Current.Language);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = Services.GetRequiredService<ShellViewModel>() };
        }

        // Обновление подписок по расписанию — в потоке окна: список серверов меняется там, где его показывают.
        Services.GetRequiredService<SubscriptionUpdater>().Start();

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Тот же состав, что у программы; тесты без экрана строят окно отсюда.</summary>
    public static ServiceProvider BuildServices(AppPaths paths, Action<IServiceCollection>? overrides = null)
    {
        var services = new ServiceCollection()
            .AddClientApplication()
            .AddClientInfrastructure(paths);
        services.AddSingleton<ToastViewModel>();
        services.AddSingleton<OverlayViewModel>();
        services.AddSingleton<ProtectionViewModel>();
        services.AddSingleton<IClipboardService, AvaloniaClipboardService>();
        services.AddSingleton<IFileSaveService, AvaloniaFileSaveService>();
        services.AddSingleton<IFileOpenService, AvaloniaFileOpenService>();
        services.AddSingleton<IUrlLauncher, AvaloniaUrlLauncher>();
        services.AddSingleton<Func<INavigator, MainPageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<MainPageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, ServersPageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<ServersPageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, ImportPageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<ImportPageViewModel>(sp, navigator));
        services.AddSingleton<Func<CheckPageViewModel>>(sp => () => ActivatorUtilities.CreateInstance<CheckPageViewModel>(sp));
        services.AddSingleton<Func<INavigator, SettingsPageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<SettingsPageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, SubscriptionsPageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<SubscriptionsPageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, ProtectionPageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<ProtectionPageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, CorePageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<CorePageViewModel>(sp, navigator));
        services.AddSingleton<ShellViewModel>();
        overrides?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
