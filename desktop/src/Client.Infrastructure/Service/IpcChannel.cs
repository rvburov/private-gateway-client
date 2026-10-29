using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Principal;
using Grpc.Net.Client;
using PrivateGateway.Client.Contracts;

namespace PrivateGateway.Client.Infrastructure.Service;

/// <summary>
/// Канал gRPC до службы: Unix-сокет или named pipe, HTTP/2 без TLS — канал локальный. На каждом
/// соединении проверяется, кто на том конце: у pipe — владелец, у Unix-сокета — uid процесса от ядра
/// ОС. Системную службу держат только SYSTEM, администраторы или root, службу пользователя — еще и
/// сам пользователь. Иначе чужая программа, занявшая адрес раньше службы, получила бы ключи серверов.
/// </summary>
public static class IpcChannel
{
    /// <summary>Сколько ждать pipe службы: pipe, которого нет, ждали бы бесконечно — службы нет, вызов падает.</summary>
    private static readonly TimeSpan PipeWait = TimeSpan.FromSeconds(2);

    /// <param name="endpoint">Имя pipe или путь к сокету (<see cref="IpcEndpoints"/>).</param>
    /// <param name="system">true — установленная служба, false — служба пользователя.</param>
    public static GrpcChannel Create(string endpoint, bool system)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = (_, cancellationToken) => ConnectAsync(endpoint, system, cancellationToken),
            // Соединение вместе с согласованием HTTP/2: на занятой машине служба отвечает не сразу. Pipe,
            // которого нет, отказывает раньше — через PipeWait.
            ConnectTimeout = TimeSpan.FromSeconds(10),
            // Канал живет, пока живет программа: соединение не закрывать по простою.
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
        };
        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = handler,
            DisposeHttpClient = true,
            MaxReceiveMessageSize = Core.Servers.SubscriptionDownload.MaxBytes + 64 * 1024,
        });
    }

    /// <summary>Отвечает ли служба по адресу за <paramref name="timeout"/>: соединение открылось и на том конце — служба.</summary>
    public static bool Answers(string endpoint, bool system, TimeSpan timeout)
    {
        using var cancel = new CancellationTokenSource(timeout);
        try
        {
            // Соединение — в пуле потоков: в потоке окна ожидание продолжения не зависнет.
            using var stream = Task.Run(() => ConnectAsync(endpoint, system, cancel.Token).AsTask(), cancel.Token).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception e) when (e is IOException or SocketException or UnauthorizedAccessException or OperationCanceledException or TimeoutException)
        {
            return false;
        }
    }

    private static async ValueTask<Stream> ConnectAsync(string endpoint, bool system, CancellationToken cancellationToken)
    {
        if (IpcEndpoints.IsPipe(endpoint) && OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync((int)PipeWait.TotalMilliseconds, cancellationToken).ConfigureAwait(false);
                CheckOwner(pipe, system);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), cancellationToken).ConfigureAwait(false);
            if (!TrustsUnix(PeerCredentials.UnixUid(socket), system, PeerCredentials.CurrentUid()))
            {
                throw new UnauthorizedAccessException("Сокет службы держит чужой процесс");
            }

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Можно ли доверять процессу с uid <paramref name="peer"/> на том конце сокета: root — всегда,
    /// сам пользователь (<paramref name="self"/>) — для службы пользователя; uid неизвестен — нельзя.
    /// </summary>
    public static bool TrustsUnix(uint? peer, bool system, uint self) => peer == 0 || (!system && peer == self);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void CheckOwner(NamedPipeClientStream pipe, bool system)
    {
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        // Объекты процесса администратора с повышенными правами принадлежат группе администраторов, а не ему самому.
        var trusted = owner is not null
            && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
                || (!system && owner == WindowsIdentity.GetCurrent().User));
        if (!trusted)
        {
            throw new UnauthorizedAccessException("Канал службы принадлежит не службе");
        }
    }
}
