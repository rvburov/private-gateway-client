using System.Net;
using System.Net.Sockets;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Service;

/// <summary>Вход прокси: слушает ли его кто-то и кто.</summary>
public sealed record EntryState(string Address, bool Listens, string? Process);

/// <summary>«Что на этом компьютере может мешать» (решение 508): входы прокси из настроек пользователя и другие программы VPN.</summary>
public sealed partial class Gateway
{
    /// <summary>Сколько адресов проверяется за раз: больше в настройках не бывает, а служба не сканер портов.</summary>
    public const int EntryLimit = 16;

    private static readonly TimeSpan EntryTimeout = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Входы прокси «узел:порт» (<paramref name="addresses"/>, не больше <see cref="EntryLimit"/>): на этой машине — по таблице
    /// сокетов, с именем процесса-владельца (нет таблицы — пробным соединением); в сети — пробным соединением. И другие
    /// программы VPN и прокси, которые сейчас работают: ядра и туннели с маршрутом ко всему интернету.
    /// </summary>
    public async Task<(IReadOnlyList<EntryState> Entries, IReadOnlyList<OtherClient> Clients)> CheckEntriesAsync(IReadOnlyList<string> addresses,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        var entries = await Task.WhenAll(addresses.Distinct(StringComparer.OrdinalIgnoreCase).Take(EntryLimit)
            .Select(a => CheckEntryAsync(a, cancellationToken))).ConfigureAwait(false);
        return ([.. entries.OfType<EntryState>()], OtherClientsNow(vpn: true, foreignProxy: null));
    }

    private async Task<EntryState?> CheckEntryAsync(string address, CancellationToken cancellationToken)
    {
        var colon = address.LastIndexOf(':');
        if (colon <= 0 || !int.TryParse(address.AsSpan(colon + 1), out var port) || port is <= 0 or > 65535)
        {
            return null;
        }

        if (OtherClients.LocalPort(address) is not null)
        {
            var (listens, owner) = Listener(port);
            return new EntryState(address, listens, owner is { } id ? NetworkInspector.ProcessName(id) : null);
        }

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(EntryTimeout);
        try
        {
            using var probe = new TcpClient();
            await probe.ConnectAsync(address[..colon].Trim('[', ']'), port, wait.Token).ConfigureAwait(false);
            return new EntryState(address, true, null);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new EntryState(address, false, null);
        }
    }
}
