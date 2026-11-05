using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Secrets;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Application.Startup;
using PrivateGateway.Client.Application.Updates;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Infrastructure.Backup;
using PrivateGateway.Client.Infrastructure.Demo;
using PrivateGateway.Client.Infrastructure.Import;
using PrivateGateway.Client.Infrastructure.Logging;
using PrivateGateway.Client.Infrastructure.Notifications;
using PrivateGateway.Client.Infrastructure.Secrets;
using PrivateGateway.Client.Infrastructure.Service;
using PrivateGateway.Client.Infrastructure.Startup;
using PrivateGateway.Client.Infrastructure.Storage;
using PrivateGateway.Client.Platform.Linux;

namespace PrivateGateway.Client.Infrastructure;

/// <summary>
/// Реализации для процесса интерфейса: хранилище профилей (SQLite), секреты в хранилище ОС,
/// журнал, клиент службы по IPC, загрузка списков серверов. Единственный слой интерфейсного
/// процесса, которому разрешена сеть, — и то только для загрузки списков серверов.
/// </summary>
public static class InfrastructureModule
{
    /// <summary>
    /// Подключение — через службу по IPC (<see cref="GatewayHost"/>): найденную при запуске или позже, в
    /// режиме прокси — службу пользователя, которую окно запускает само. Демо-режим (<paramref name="demo"/>,
    /// <c>--demo</c>) — имитация <see cref="SimulatedGatewayClient"/> и, пока своих серверов нет, демо-серверы
    /// (<see cref="UserOrDemoCatalog"/>).
    /// </summary>
    public static IServiceCollection AddClientInfrastructure(this IServiceCollection services, AppPaths paths, bool demo = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(paths);
        services.AddSingleton(_ => LoggingSetup.CreateFactory(paths));
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<ISettingsStore, SqliteSettingsStore>();
        services.AddSingleton<IExternalProxyStore, ExternalProxyStore>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IUserProxySettings>(_ => UserProxySettings.ForCurrentUser());
        services.AddSingleton<IMachineSettings>(sp =>
            MachineSettings.ForCurrentUser(sp.GetRequiredService<IUserProxySettings>(), paths.DataDirectory, sp.GetRequiredService<TimeProvider>()));
        // Прокси вошедшего пользователя ставит окно: установленная служба (SYSTEM, root) его настроек не видит (решения 516, 574).
        var userProxy = new Lazy<ISystemProxy?>(() => demo ? null
            : OperatingSystem.IsWindows() ? new CurrentUserWindowsProxy()
            : OperatingSystem.IsLinux() ? (ISystemProxy?)KdeSystemProxy.ForThisSession() ?? GnomeSystemProxy.ForThisSession()
            : null);
        services.AddSingleton<IGatewayClient>(sp => demo
            ? ActivatorUtilities.CreateInstance<SimulatedGatewayClient>(sp)
            : new GatewayHost(ServiceLocator.ForThisWindow(sp.GetRequiredService<TimeProvider>()),
                address => new ServiceGatewayClient(IpcChannel.Create(address.Endpoint, address.System),
                    address.System ? ServiceKind.Installed : ServiceKind.User,
                    id => Definition(sp, id),
                    () => sp.GetRequiredService<SettingsService>().Current, sp.GetRequiredService<TimeProvider>(),
                    sp.GetRequiredService<IUserProxySettings>().Read, manageUserProxy: userProxy.Value is not null,
                    selectedTarget: () => sp.GetRequiredService<ConnectionTarget>().Selected),
                () => sp.GetRequiredService<SettingsService>().Current, sp.GetRequiredService<TimeProvider>(),
                () => File.Exists(UserServiceFiles.ProxyBackup(UserServiceFiles.Folder))
                    || new FileUserProxyBackupStore(paths.UserProxyBackup).IsUserServiceBackup(), sp.GetRequiredService<IUserProxySettings>(),
                selectedTarget: () => sp.GetRequiredService<ConnectionTarget>().Selected));
        services.AddSingleton<IUserProxyController>(sp => new UserProxyController(sp.GetRequiredService<SettingsService>(),
            sp.GetRequiredService<IGatewayClient>(), userProxy.Value,
            new FileUserProxyBackupStore(paths.UserProxyBackup), sp.GetRequiredService<TimeProvider>(), demo ? null : RunOnceProxyRecovery.ForThisWindow()));
        services.AddSingleton<IServiceInstaller>(sp => demo
            ? new ServiceInstaller(null, (_, _) => Task.FromResult<int?>(1), () => false, sp.GetRequiredService<TimeProvider>())
            : ServiceInstaller.ForThisWindow(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IAppRelauncher>(_ => demo ? new AppRelauncher(null) : AppRelauncher.ForThisWindow());
        services.AddSingleton(sp => SecretStores.ForCurrentOs(sp.GetRequiredService<SqliteDatabase>()));
        services.AddSingleton<SqliteServerCatalog>();
        services.AddSingleton<IServerStore>(sp => sp.GetRequiredService<SqliteServerCatalog>());
        services.AddSingleton<DemoServerCatalog>();
        services.AddSingleton<IServerCatalog, UserOrDemoCatalog>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<IAutostart>(_ => OsAutostart.ForCurrentUser());
        services.AddSingleton(_ => SystemNotifiers.ForCurrentOs(File.Exists(paths.NotifyIcon) ? paths.NotifyIcon : null));
        services.AddSingleton<ISubscriptionDownloader>(sp =>
            new HttpSubscriptionDownloader(HttpSubscriptionDownloader.CreateClient(), sp.GetRequiredService<TimeProvider>(),
                HttpSubscriptionDownloader.CreateDirectClient(),
                url => HttpSubscriptionDownloader.IsForeignProxy(HttpClient.DefaultProxy, url,
                    sp.GetRequiredService<SettingsService>().Current.Connection.ProxyPort)));
        return services;
    }

    private static Core.Servers.ServerDefinition? Definition(IServiceProvider services, string id)
    {
        if (!Core.Settings.ExternalProxySettings.IsExternalId(id))
        {
            return services.GetRequiredService<SqliteServerCatalog>().Definition(id);
        }

        var settings = services.GetRequiredService<SettingsService>();
        var before = settings.Current.ExternalProxy;
        if (before.ToProfile()?.Id != id)
        {
            return null;
        }

        var definition = services.GetRequiredService<IExternalProxyStore>().Definition();
        return settings.Current.ExternalProxy == before ? definition : null;
    }
}
