using System.Net;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Service;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Системный прокси не стоит между окном и службой. У пользователя на Windows (2026-10-30) прокси оставил выключенный
/// другой VPN-клиент, и канал gRPC шел к нему: HTTP/2 без TLS через HTTP-прокси невозможен — «unable to establish HTTP/2
/// connection», служба работала, а окно писало «Служба не запущена». Тест меняет прокси процесса — поэтому один.
/// </summary>
[Collection(ProcessProxySerial.Name)]
public sealed class IpcProxyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Window_reaches_the_service_past_a_dead_system_proxy()
    {
        await using var service = await RunningService.StartAsync();
        var saved = HttpClient.DefaultProxy;
        // Порт 9 (discard) на 127.0.0.1 никто не слушает: прокси выключенной программы.
        HttpClient.DefaultProxy = new WebProxy("http://127.0.0.1:9");
        try
        {
            using var client = new ServiceGatewayClient(IpcChannel.Create(service.Endpoint, system: false), ServiceKind.User, _ => null,
                () => AppSettings.Default, TimeProvider.System);

            (await client.GetDetailsAsync(Ct)).ShouldNotBeNull("служба отвечает и при мертвом системном прокси");
        }
        finally
        {
            HttpClient.DefaultProxy = saved;
        }
    }
}

/// <summary>Тесты, которые меняют прокси всего процесса (<see cref="HttpClient.DefaultProxy"/>), — без соседей.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessProxySerial
{
    public const string Name = "Прокси процесса";
}
