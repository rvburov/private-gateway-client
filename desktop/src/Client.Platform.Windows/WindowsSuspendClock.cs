using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Platform.Windows;

/// <summary>
/// Сон на Windows: время прерываний со сном (QueryInterruptTime) минус без сна (QueryUnbiasedInterruptTime, читается
/// первым), по 100 нс.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSuspendClock : ISuspendClock
{
    public TimeSpan Suspended
    {
        get
        {
            QueryUnbiasedInterruptTime(out var unbiased);
            QueryInterruptTime(out var biased);
            return SuspendTime.Between(unbiased, biased, numerator: 100, denominator: 1);
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
