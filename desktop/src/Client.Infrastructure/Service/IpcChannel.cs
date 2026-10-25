using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Principal;
using Grpc.Net.Client;
using PrivateGateway.Client.Contracts;

namespace PrivateGateway.Client.Infrastructure.Service;

/// <summary>
/// Канал gRPC до службы: Unix-сокет или named pipe, HTTP/2 без TLS — канал локальный. У pipe
/// проверяется владелец: системную службу держат SYSTEM или администраторы, службу пользователя —
/// сам пользователь. Иначе чужая программа, занявшая имя раньше службы, получила бы ключи серверов.
/// </summary>
public static class IpcChannel
{
    public static GrpcChannel Create(string endpoint, bool system)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = (_, cancellationToken) => ConnectAsync(endpoint, system, cancellationToken),
            // Канал живет, пока живет программа: соединение не закрывать по простою.
            PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
            KeepAlivePingDelay = TimeSpan.FromSeconds(30),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
        };
        return GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
    }

    private static async ValueTask<Stream> ConnectAsync(string endpoint, bool system, CancellationToken cancellationToken)
    {
        if (IpcEndpoints.IsPipe(endpoint) && OperatingSystem.IsWindows())
        {
            var pipe = new NamedPipeClientStream(".", endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(cancellationToken).ConfigureAwait(false);
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
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void CheckOwner(NamedPipeClientStream pipe, bool system)
    {
        var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        var trusted = system
            ? owner is not null && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
            : owner is not null && owner == WindowsIdentity.GetCurrent().User;
        if (!trusted)
        {
            throw new UnauthorizedAccessException("Канал службы принадлежит не службе");
        }
    }
}
