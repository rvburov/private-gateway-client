using System.Diagnostics;
using System.Globalization;

namespace PrivateGateway.Client.UI.Services;

/// <summary>Замеры запуска только по PG_STARTUP_TRACE=1: без адресов, реквизитов и изменения настроек ОС.</summary>
internal static class StartupTiming
{
    private static readonly long Started = Stopwatch.GetTimestamp();
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("PG_STARTUP_TRACE") == "1";

    public static void Mark(string stage)
    {
        if (Enabled)
        {
            Console.WriteLine("startup " + stage + " " + Stopwatch.GetElapsedTime(Started).TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms");
        }
    }
}
