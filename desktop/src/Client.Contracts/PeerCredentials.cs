using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace PrivateGateway.Client.Contracts;

/// <summary>
/// Кто на том конце Unix-сокета: uid процесса, его сообщает ядро ОС, подделать его нельзя. Служба
/// так проверяет интерфейс, интерфейс — службу. Linux — SO_PEERCRED (struct ucred), macOS —
/// LOCAL_PEERCRED (struct xucred).
/// </summary>
public static class PeerCredentials
{
    private const int SolSocket = 1;
    private const int SoPeerCred = 17;
    private const int SolLocal = 0;
    private const int LocalPeerCred = 1;

    /// <summary>uid собеседника; null — ОС не сообщила (не Unix-сокет или не Linux/macOS).</summary>
    public static uint? UnixUid(Socket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        try
        {
            if (OperatingSystem.IsLinux())
            {
                // struct ucred { pid_t pid; uid_t uid; gid_t gid; }
                var ucred = new byte[12];
                return socket.GetRawSocketOption(SolSocket, SoPeerCred, ucred) >= 8
                    ? BinaryPrimitives.ReadUInt32LittleEndian(ucred.AsSpan(4))
                    : null;
            }

            if (OperatingSystem.IsMacOS())
            {
                // struct xucred { u_int cr_version; uid_t cr_uid; short cr_ngroups; gid_t cr_groups[16]; }
                var xucred = new byte[76];
                return socket.GetRawSocketOption(SolLocal, LocalPeerCred, xucred) >= 8
                    ? BinaryPrimitives.ReadUInt32LittleEndian(xucred.AsSpan(4))
                    : null;
            }
        }
        catch (SocketException)
        {
        }

        return null;
    }

    /// <summary>uid своего процесса (geteuid) — на Linux и macOS.</summary>
    public static uint CurrentUid() => OperatingSystem.IsWindows() ? 0 : GetEffectiveUid();

    // Без параметров и преобразований — LibraryImport (и небезопасный код) не нужен.
    [DllImport("libc", EntryPoint = "geteuid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
#pragma warning disable SYSLIB1054
    private static extern uint GetEffectiveUid();
#pragma warning restore SYSLIB1054
}
