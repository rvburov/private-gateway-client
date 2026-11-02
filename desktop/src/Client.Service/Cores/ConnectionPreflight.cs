using System.Net;
using System.Net.Sockets;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Service.Cores;

/// <summary>Результат свежей проверки профиля: ответ HTTP и адрес, которым удалось воспользоваться.</summary>
public sealed record ConnectionPreflightResult(ProbeReachability Reachability, IPAddress? Address = null, bool UseOutsideBinding = true);

/// <summary>
/// Проверка выбранного профиля до TUN и новой блокировки. Запускает только локальные парольные входы Xray, с теми же
/// параметрами транспорта и закрепленным адресом сервера, что у VPN. DNS ОС, маршруты и системный прокси не меняются.
/// Отказ первого адреса не закрепляет неработающий адрес на всех следующих попытках: остальные проверяются по очереди
/// в пределах одного общего срока. Само наличие ответа TCP или старое значение задержки успехом не считается.
/// </summary>
public static class ConnectionPreflight
{
    // Холодный запуск ядра и рукопожатие не должны получить доли секунды только из-за длинного ответа DNS.
    // Общий срок не продлевается: если его не хватило, часть адресов останется непроверенной.
    private static readonly TimeSpan MinimumCandidateTime = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Проверить разрешенные адреса. <paramref name="probe"/> — подмена одной адресной пробы (тесты); null — настоящее ядро.
    /// Ожидание и запуск ядер входят в <paramref name="timeout"/>; отмена вызывающего остается отменой.
    /// </summary>
    public static async Task<ConnectionPreflightResult> CheckAsync(
        string xray,
        string? singBox,
        CoreRequest request,
        IReadOnlyList<IPAddress> addresses,
        Uri target,
        TimeSpan timeout,
        TimeProvider time,
        string? outside = null,
        Func<CoreRequest, IPAddress, CancellationToken, Task<ProbeReachability>>? probe = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(addresses);
        ArgumentNullException.ThrowIfNull(time);
        if (addresses.Count == 0)
        {
            return new(new ProbeReachability(null, ProbeFailure.NameResolution));
        }

        using var deadline = new CancellationTokenSource(timeout, time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var started = time.GetTimestamp();
        var candidates = Candidates(addresses);
        // Привязка сокета бывает несовместима с конкретным адаптером или изменившейся сетью. Запасной путь проверяет
        // тот же профиль и IP, без изменения TLS/учетных данных. После TUN ему остается точное правило server-direct.
        var fallback = probe is null && outside is { Length: > 0 } && !XrayConfigBuilder.NeedsRelay(request.Server);
        var paths = fallback ? 2 : 1;
        var last = ProbeReachability.TimedOut();
        try
        {
            for (var index = 0; index < candidates.Count; index++)
            {
                for (var path = 0; path < paths; path++)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    var remaining = timeout - time.GetElapsedTime(started);
                    if (remaining <= TimeSpan.Zero)
                    {
                        return new(ProbeReachability.TimedOut());
                    }

                    // Быстрый отказ оставляет время следующему пути; медленная привязка не забирает весь общий срок.
                    var remainingPaths = ((candidates.Count - index) * paths) - path;
                    var budget = TimeSpan.FromTicks(Math.Min(remaining.Ticks,
                        Math.Max(MinimumCandidateTime.Ticks, remaining.Ticks / remainingPaths)));
                    using var candidateDeadline = new CancellationTokenSource(budget, time);
                    using var candidateStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, candidateDeadline.Token);
                    var binding = path == 0 ? outside : string.Empty;
                    try
                    {
                        var attempt = probe is null
                            ? CheckAddressAsync(xray, singBox, request, candidates[index], target, budget, time, binding, candidateStop.Token)
                            : probe(request, candidates[index], candidateStop.Token);
                        // Настоящая проба сначала завершает finally и снимает свое ядро. Подмена тоже ограничена сроком,
                        // даже если проигнорировала переданный токен; она не владеет процессом ядра.
                        last = probe is null
                            ? await attempt.ConfigureAwait(false)
                            : await attempt.WaitAsync(candidateStop.Token).ConfigureAwait(false);
                        candidateStop.Token.ThrowIfCancellationRequested();
                        if (last.Reached)
                        {
                            return new(last, candidates[index], !fallback || path == 0);
                        }
                    }
                    catch (OperationCanceledException) when (!stop.IsCancellationRequested && candidateDeadline.IsCancellationRequested)
                    {
                        last = ProbeReachability.TimedOut();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return new(ProbeReachability.TimedOut());
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(last);
    }

    private static async Task<ProbeReachability> CheckAddressAsync(string xray, string? singBox, CoreRequest request,
        IPAddress address, Uri target, TimeSpan timeout, TimeProvider time, string? outside, CancellationToken cancellationToken)
    {
        // Фиксированный пользовательский вход на время проверки не нужен: он мог бы занять его порт или изменить браузер.
        var checking = request with { Connection = request.Connection with { VpnSystemProxy = false, CoreLog = CoreLogLevel.Warning } };
        using var tunnelPort = LocalPort.ReserveForTcpAndUdp();
        using var probePort = LocalPort.ReserveForTcpAndUdp();
        var link = new TunLink(tunnelPort.Port, VpnTunnel.Secret(), address.ToString(), Outside: outside);
        var entry = new ProbeLink(probePort.Port, VpnTunnel.Secret(), VpnTunnel.Secret());
        var relay = CoreRelay.LinkFor(checking.Server, singBox);
        var built = XrayConfigBuilder.Build(checking, link, entry, relay);
        if (built.Config is null)
        {
            throw new TunnelException("Ядро не поддерживает параметры выбранного профиля");
        }

        tunnelPort.Dispose();
        probePort.Dispose();
        CoreProcess? core = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            core = CoreRelay.StartXray(xray, built.Config, singBox,
                relay is null ? [] : [new RelayRoute(checking.Server, relay, address.ToString())], CoreLogLevel.Warning, out var relayReady);
            while (!core.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (relayReady() && await ListensAsync(entry.Port, cancellationToken).ConfigureAwait(false))
                {
                    return await LatencyProbe.CheckAsync(new ServerProbe(entry.Port,
                        new NetworkCredential(entry.User, entry.Password), target, timeout), cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), time, cancellationToken).ConfigureAwait(false);
            }

            throw new TunnelException("Ядро проверки профиля остановилось до готовности");
        }
        finally
        {
            if (core is not null)
            {
                try
                {
                    // Здесь нет TUN и маршрутов, поэтому ожидание graceful shutdown не нужно.
                    await core.StopAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    core.Dispose();
                }
            }
        }
    }

    private static async Task<bool> ListensAsync(int port, CancellationToken cancellationToken)
    {
        using var socket = new TcpClient();
        try
        {
            await socket.ConnectAsync(IPAddress.Loopback, port, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static List<IPAddress> Candidates(IReadOnlyList<IPAddress> addresses)
    {
        var first = addresses[0].AddressFamily;
        var same = new Queue<IPAddress>(addresses.Distinct().Where(a => a.AddressFamily == first));
        var other = new Queue<IPAddress>(addresses.Distinct().Where(a => a.AddressFamily != first));
        var ordered = new List<IPAddress>();
        while (same.Count > 0 || other.Count > 0)
        {
            if (same.TryDequeue(out var next))
            {
                ordered.Add(next);
            }

            if (other.TryDequeue(out next))
            {
                ordered.Add(next);
            }
        }

        return ordered;
    }
}
