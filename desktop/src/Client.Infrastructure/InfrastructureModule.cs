using Microsoft.Extensions.DependencyInjection;

namespace PrivateGateway.Client.Infrastructure;

/// <summary>
/// Реализации для процесса интерфейса: хранилище профилей (SQLite), секреты в хранилище ОС,
/// журнал, клиент службы по IPC, загрузка списков серверов. Единственный слой интерфейсного
/// процесса, которому разрешена сеть, — и то только для загрузки списков серверов.
/// </summary>
public static class InfrastructureModule
{
    public static IServiceCollection AddClientInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
