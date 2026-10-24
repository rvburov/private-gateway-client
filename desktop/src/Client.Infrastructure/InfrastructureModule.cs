using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Infrastructure.Demo;

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
    public static IServiceCollection AddClientInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IGatewayClient, SimulatedGatewayClient>();
        services.AddSingleton<IServerCatalog, DemoServerCatalog>();
        return services;
    }
}
