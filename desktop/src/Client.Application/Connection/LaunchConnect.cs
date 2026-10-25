using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;

namespace PrivateGateway.Client.Application.Connection;

/// <summary>
/// «Подключаться при запуске» («Настройки»): окно открылось — включить защиту через выбранный сервер, если
/// она еще не включена. Служба могла держать защиту и без окна (установленная служба) — состояние приходит
/// от нее потоком, поэтому сначала дождаться его, чтобы не переподключать работающую защиту. «Блокировка»
/// и «Ошибка» — тоже повод подключиться: служба перезапустилась после сбоя с блоком при обрыве, и
/// подключение возвращает защиту с теми же настройками. До конца первого запуска — ничего.
/// </summary>
public sealed class LaunchConnect(SettingsService settings, IGatewayClient gateway, IServerCatalog catalog, TimeProvider time)
{
    /// <summary>Сколько ждать первого состояния от службы.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(3);

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        if (settings.Current is not { ConnectOnLaunch: true, OnboardingDone: true } || catalog.Selected is not { } server)
        {
            return;
        }

        if (gateway.Service != ServiceKind.None)
        {
            await FirstStatusAsync(cancellationToken).ConfigureAwait(false);
        }

        if (gateway.Status.State is ConnectionState.Off or ConnectionState.Blocked or ConnectionState.Error)
        {
            await gateway.ConnectAsync(server, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FirstStatusAsync(CancellationToken cancellationToken)
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStatus(object? sender, ConnectionStatus status) => first.TrySetResult();
        gateway.StatusChanged += OnStatus;
        try
        {
            await Task.WhenAny(first.Task, Task.Delay(Wait, time, cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            gateway.StatusChanged -= OnStatus;
        }
    }
}
