using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Platform.Windows;

/// <summary>Сон на Windows: время прерываний со сном (QueryInterruptTime) минус без сна (QueryUnbiasedInterruptTime), по 100 нс.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSuspendClock : ISuspendClock
{
    public TimeSpan Suspended
    {
        get
        {
            QueryInterruptTime(out var biased);
            QueryUnbiasedInterruptTime(out var unbiased);
            return TimeSpan.FromTicks((long)(biased - unbiased));
        }
    }

    [DllImport("api-ms-win-core-realtime-l1-1-1.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void QueryInterruptTime(out ulong time);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong time);
}
