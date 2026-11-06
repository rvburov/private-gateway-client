using Grpc.Core;
using Grpc.Net.Client;
using PrivateGateway.Client.Network.Xray;
using PrivateGateway.Client.Service.Cores.XrayApi;

namespace PrivateGateway.Client.Service.Cores;

/// <summary>
/// Счетчики байт ядра Xray для общей скорости на главной (ROADMAP: «Суммарная скорость совместного TUN и HTTP/SOCKS»): сумма
/// по входам <see cref="XrayConfigBuilder.CountedInbounds"/> из StatsService ядра на 127.0.0.1 (<see cref="ProbeLink.StatsPort"/>).
/// Счетчик адаптера TUN не видит прямой локальный вход HTTP/SOCKS режима VPN, а в режиме прокси адаптера нет вовсе — ядро
/// считает и то и другое. Канал живет, пока живет подключение: замер раз в 2 с не открывает соединение заново.
/// </summary>
public sealed class XrayStatsClient(int port) : IDisposable
{
    /// <summary>Сколько ждать ответа ядра: замер — подсказка на экране, ждать дольше незачем.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    private readonly GrpcChannel _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{port}", new GrpcChannelOptions
    {
        // Без системного прокси: адрес — свое ядро на этой машине.
        HttpHandler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = Timeout },
        DisposeHttpClient = true,
    });

    public int Port { get; } = port;

    /// <summary>Принято и отправлено через счетные входы с запуска ядра; ядро не ответило — null.</summary>
    public async Task<(long Received, long Sent)?> TotalsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var client = new StatsService.StatsServiceClient(_channel);
            var reply = await client.QueryStatsAsync(new QueryStatsRequest { Pattern = "inbound>>>" }, deadline: DateTime.UtcNow + Timeout,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return Sum(reply.Stat.Select(stat => (stat.Name, stat.Value)));
        }
        catch (RpcException)
        {
            return null;
        }
    }

    /// <summary>
    /// Сумма по счетным входам: имя счетчика — «inbound&gt;&gt;&gt;тег&gt;&gt;&gt;traffic&gt;&gt;&gt;uplink|downlink»; отданное
    /// программам (downlink) — принятое, их отдача (uplink) — отправленное. Чужие и непонятные имена не в счет.
    /// </summary>
    public static (long Received, long Sent) Sum(IEnumerable<(string Name, long Value)> stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        long received = 0, sent = 0;
        foreach (var (name, value) in stats)
        {
            var parts = name.Split(">>>");
            if (parts is not ["inbound", var tag, "traffic", var direction] || !XrayConfigBuilder.CountedInbounds.Contains(tag))
            {
                continue;
            }

            if (direction == "downlink")
            {
                received += value;
            }
            else if (direction == "uplink")
            {
                sent += value;
            }
        }

        return (received, sent);
    }

    public void Dispose() => _channel.Dispose();
}
