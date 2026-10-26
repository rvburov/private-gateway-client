using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Platform.MacOS;

/// <summary>
/// Сон на macOS: часы Mach со сном (mach_continuous_time) минус без сна (mach_absolute_time), в наносекундах по
/// mach_timebase_info.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacSuspendClock : ISuspendClock
{
    private readonly (uint Numer, uint Denom) _timebase = mach_timebase_info(out var info) == 0 ? (info.Numer, info.Denom) : (1, 1);

    public TimeSpan Suspended
    {
        get
        {
            var ticks = mach_continuous_time() - mach_absolute_time();
            return TimeSpan.FromTicks((long)(ticks * _timebase.Numer / _timebase.Denom / 100));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Timebase
    {
        public uint Numer;
        public uint Denom;
    }

    // Без указателей и строк — LibraryImport (и небезопасный код) не нужен.
    [DllImport("/usr/lib/libSystem.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern ulong mach_absolute_time();

    [DllImport("/usr/lib/libSystem.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern ulong mach_continuous_time();

    [DllImport("/usr/lib/libSystem.dylib")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int mach_timebase_info(out Timebase info);
}
