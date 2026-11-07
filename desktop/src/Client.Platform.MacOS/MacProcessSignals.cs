using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PrivateGateway.Client.Platform.MacOS;

[SupportedOSPlatform("macos")]
public static class MacProcessSignals
{
    private const int SigUnblock = 2;
    private const int SigChild = 20;

    /// <summary>После запроса прав через osascript .NET должна получать SIGCHLD для ожидания дочерних процессов.</summary>
    public static void UnblockChildExit()
    {
        // В Darwin sigset_t — 32-битная маска, сигнал N занимает бит N - 1.
        uint mask = 1u << (SigChild - 1);
        var error = pthread_sigmask(SigUnblock, in mask, IntPtr.Zero);
        if (error != 0)
        {
            throw new Win32Exception(error);
        }
    }

    [DllImport("/usr/lib/libSystem.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int pthread_sigmask(int how, in uint set, IntPtr oldSet);
}
