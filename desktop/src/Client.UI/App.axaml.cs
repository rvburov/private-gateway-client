using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Notifications;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Application.Startup;
using PrivateGateway.Client.Application.Updates;
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

    /// <summary>С чем запущена программа: папка данных, замок одной копии, запуск в трей.</summary>
    internal static AppStartup? Startup { get; set; }

    public override void OnFrameworkInitializationCompleted()
    {
        // Без настольного жизненного цикла (дизайнер, headless) настоящую службу и фоновые задачи не запускаем.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var startup = Startup ?? new AppStartup(AppPaths.ForCurrentUser(), null, Minimized: false, Demo: false);
            var window = new MainWindow();
            var stop = new CancellationTokenSource();
            var ready = false;
            window.Closed += (_, _) =>
            {
                if (!ready)
                {
                    stop.Cancel();
                    desktop.Shutdown();
                }
            };
            desktop.Exit += (_, _) => stop.Cancel();
            _ = StartDesktopAsync(desktop, window, startup, () => ready = true, stop.Token);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartDesktopAsync(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window, AppStartup startup,
        Action ready, CancellationToken cancellationToken)
    {
        try
        {
            await window.StartAsync(async () =>
            {
                Services = BuildServices(startup.Paths, demo: startup.Demo);
                var settings = Services.GetRequiredService<SettingsService>();
                // Миграции SQLite и поиск IPC могут ждать диск или службу. Первый кадр уже показан, поток окна свободен.
                await Task.Run(() =>
                {
                    _ = settings.Current;
                    _ = Services.GetRequiredService<IGatewayClient>();
                    _ = Services.GetRequiredService<IServerCatalog>().Servers;
                }, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Loc.Instance.SetLanguage(settings.Current.Language);
                window.DataContext = Services.GetRequiredService<ShellViewModel>();
                _ = new DesktopHost(this, desktop, window, Services.GetRequiredService<TrayViewModel>(), settings, startup.Instance,
                    startup.Minimized, Services.GetRequiredService<NoticeWindow>(), Services.GetRequiredService<UpdateWatcher>());
                desktop.Exit += (_, _) => Services.GetRequiredService<IUserProxyController>().Shutdown();
                ready();
                StartupTiming.Mark("ready");
                if (startup.CheckTray)
                {
                    MacStatusItem.CheckLifetime(desktop);
                }
                else
                {
                    StartBackground(startup.Paths);
                }
            }, show: !startup.Minimized, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            (Services as IDisposable)?.Dispose();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(Core.Logging.SecretMasker.Mask("Запуск окна: " + error));
            window.ShowStartupFailure(() => desktop.Shutdown());
        }
    }

    private void StartBackground(AppPaths paths)
    {
        NotifyIcon.Write(paths);
        Services.GetRequiredService<NoticeActions>().Start();
        // Подписки и подписчики состояния запускаются в потоке окна после его подготовки.
        Services.GetRequiredService<SubscriptionUpdater>().Start();
        Services.GetRequiredService<AutostartSync>().Start();
        Services.GetRequiredService<ProtectionSync>().Start();
        Services.GetRequiredService<ExitMemory>().Start();
        Services.GetRequiredService<PrivateGateway.Client.Infrastructure.Storage.CountryServerCatalog>().Start();
        Services.GetRequiredService<IUserProxyController>().Start();
        Services.GetRequiredService<NetworkEventLog>().Start();
        Services.GetRequiredService<UpdateWatcher>().Start();
        _ = Services.GetRequiredService<LaunchConnect>().RunAsync();
    }

    /// <summary>Тот же состав, что у программы; тесты без экрана строят окно отсюда (в демо-режиме).</summary>
    public static ServiceProvider BuildServices(AppPaths paths, Action<IServiceCollection>? overrides = null, bool demo = false)
    {
        var services = new ServiceCollection()
            .AddClientApplication()
            .AddClientInfrastructure(paths, demo);
        services.AddSingleton<ToastViewModel>();
        services.AddSingleton<OverlayViewModel>();
        services.AddSingleton<ProtectionViewModel>();
        services.AddSingleton<CheckFocus>();
        services.AddSingleton<IClipboardService, AvaloniaClipboardService>();
        services.AddSingleton<IFileSaveService, AvaloniaFileSaveService>();
        services.AddSingleton<IFileOpenService, AvaloniaFileOpenService>();
        services.AddSingleton<IUrlLauncher, AvaloniaUrlLauncher>();
        services.AddSingleton<Func<INavigator, MainPageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<MainPageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, ServersPageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<ServersPageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, ImportPageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<ImportPageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, CheckPageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<CheckPageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, NetworkPageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<NetworkPageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, ProbePageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<ProbePageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, ConnectionsPageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<ConnectionsPageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, ComparePageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<ComparePageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, PathPageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<PathPageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, LayersPageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<LayersPageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, EventsPageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<EventsPageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, TroublePageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<TroublePageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, VpnPathPageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<VpnPathPageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, SettingsPageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<SettingsPageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, SubscriptionsPageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<SubscriptionsPageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, ProtectionPageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<ProtectionPageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, CorePageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<CorePageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, RoutingPageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<RoutingPageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, BackupPageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<BackupPageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, AboutPageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<AboutPageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, WelcomePageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<WelcomePageViewModel>(sp, navigator));
        services.AddSingleton<Func<INavigator, ProblemPageViewModel>>(sp => navigator => ActivatorUtilities.CreateInstance<ProblemPageViewModel>(sp, navigator));
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<TrayViewModel>();
        services.AddSingleton<INoticeTexts, NoticeTexts>();
        services.AddSingleton<NoticeWindow>();
        services.AddSingleton<INoticeWindow>(sp => sp.GetRequiredService<NoticeWindow>());
        services.AddSingleton<NoticeActions>();
        overrides?.Invoke(services);
        return services.BuildServiceProvider();
    }
}

/// <summary>С чем запущена программа: папка данных, замок одной копии (null — в дизайнере), запуск в трей, демо-режим.</summary>
internal sealed record AppStartup(AppPaths Paths, SingleInstance? Instance, bool Minimized, bool Demo, bool CheckTray = false);
