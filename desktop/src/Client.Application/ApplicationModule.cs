using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Connection;

namespace PrivateGateway.Client.Application;

/// <summary>
/// Регистрация сценариев программы: подключение, смена сервера, импорт, обновление серверов.
/// Сценарии не знают ни об Avalonia, ни о сети — только о моделях ядра и контракте службы.
/// </summary>
public static class ApplicationModule
{
    public static IServiceCollection AddClientApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ConnectionController>();
        return services;
    }
}
