using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Infrastructure.Demo;
using PrivateGateway.Client.Infrastructure.Logging;
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
    /// Пока службы и базы нет (этап 1), интерфейс работает на имитации: <see cref="SimulatedGatewayClient"/>
    /// и <see cref="DemoServerCatalog"/>.
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
        services.AddSingleton<IServerCatalog, DemoServerCatalog>();
        return services;
    }
}
