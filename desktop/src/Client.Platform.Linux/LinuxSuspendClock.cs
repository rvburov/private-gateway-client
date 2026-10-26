using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Platform.Linux;

/// <summary>Сон на Linux: часы с загрузки со сном (CLOCK_BOOTTIME) минус те же часы без сна (CLOCK_MONOTONIC).</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxSuspendClock : ISuspendClock
{
    private const int ClockMonotonic = 1;
    private const int ClockBoottime = 7;

    public TimeSpan Suspended => Read(ClockBoottime) - Read(ClockMonotonic);

    private static TimeSpan Read(int clock) => clock_gettime(clock, out var time) == 0
        ? TimeSpan.FromTicks((time.Seconds * TimeSpan.TicksPerSecond) + (time.Nanoseconds / 100))
        : throw new IOException("clock_gettime: ошибка " + Marshal.GetLastPInvokeError());

    [StructLayout(LayoutKind.Sequential)]
    private struct Timespec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    // Структура без указателей и строк — LibraryImport (и небезопасный код) не нужен.
    [DllImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int clock_gettime(int clock, out Timespec time);
}
