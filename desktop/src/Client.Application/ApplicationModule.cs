using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Account;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Notifications;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Application.Startup;
using PrivateGateway.Client.Application.Updates;

namespace PrivateGateway.Client.Application;

/// <summary>
/// Регистрация сценариев программы: подключение, смена сервера, импорт, обновление серверов, вход в аккаунт сайта.
/// Сценарии не знают ни об Avalonia, ни о сети — только о моделях ядра и контракте службы.
/// </summary>
public static class ApplicationModule
{
    public static IServiceCollection AddClientApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<ConnectionTarget>();
        services.AddSingleton<ConnectionController>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<SubscriptionUpdater>();
        services.AddSingleton<AutostartSync>();
        services.AddSingleton<ProtectionSync>();
        services.AddSingleton<ExitMemory>();
        services.AddSingleton<NetworkEventLog>();
        services.AddSingleton<LaunchConnect>();
        services.AddSingleton<NotificationCenter>();
        services.AddSingleton<UpdateWatcher>();
        services.AddSingleton<AccountService>();
        return services;
    }
}
