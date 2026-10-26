using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Notifications;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Application.Startup;

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
        services.AddSingleton<SettingsService>();
        services.AddSingleton<SubscriptionUpdater>();
        services.AddSingleton<AutostartSync>();
        services.AddSingleton<ProtectionSync>();
        services.AddSingleton<LaunchConnect>();
        services.AddSingleton<NotificationCenter>();
        return services;
    }
}
