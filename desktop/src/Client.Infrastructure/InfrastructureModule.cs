using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Secrets;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Infrastructure.Demo;
using PrivateGateway.Client.Infrastructure.Import;
using PrivateGateway.Client.Infrastructure.Logging;
using PrivateGateway.Client.Infrastructure.Secrets;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Infrastructure;

/// <summary>
/// Реализации для процесса интерфейса: хранилище профилей (SQLite), секреты в хранилище ОС,
/// журнал, клиент службы по IPC, загрузка списков серверов. Единственный слой интерфейсного
/// процесса, которому разрешена сеть, — и то только для загрузки списков серверов.
/// </summary>
public static class InfrastructureModule
{
    /// <summary>
    /// Пока службы нет (этап 1), подключение — имитация <see cref="SimulatedGatewayClient"/>. Серверы —
    /// свои из базы, а пока их нет — демо-серверы (<see cref="UserOrDemoCatalog"/>).
    /// </summary>
    public static IServiceCollection AddClientInfrastructure(this IServiceCollection services, AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(paths);
        services.AddSingleton(_ => LoggingSetup.CreateFactory(paths));
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton<SqliteDatabase>();
        services.AddSingleton<ISettingsStore, SqliteSettingsStore>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IGatewayClient, SimulatedGatewayClient>();
        services.AddSingleton(sp => SecretStores.ForCurrentOs(sp.GetRequiredService<SqliteDatabase>()));
        services.AddSingleton<SqliteServerCatalog>();
        services.AddSingleton<IServerStore>(sp => sp.GetRequiredService<SqliteServerCatalog>());
        services.AddSingleton<DemoServerCatalog>();
        services.AddSingleton<IServerCatalog, UserOrDemoCatalog>();
        services.AddSingleton<ISubscriptionDownloader>(sp =>
            new HttpSubscriptionDownloader(HttpSubscriptionDownloader.CreateClient(), sp.GetRequiredService<TimeProvider>()));
        return services;
    }
}
