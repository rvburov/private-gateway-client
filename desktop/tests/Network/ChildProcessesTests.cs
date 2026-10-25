using System.Diagnostics;
using PrivateGateway.Client.Platform.Windows;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Задание Windows для ядер: служба упала — ОС закрывает задание и снимает ядра, а не оставляет туннель
/// без хозяина. Только на Windows (CI): «ядро» — ping, который иначе шел бы еще полминуты.
/// </summary>
public sealed class ChildProcessesTests
{
    [Fact]
    public void Closing_the_job_ends_its_processes_and_only_them()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("задания — только в Windows");
            return;
        }

        var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        using var inJob = Start(ping);
        using var outside = Start(ping);
        try
        {
            var job = new ChildProcesses();
            job.Add(inJob);

            job.Dispose();

            inJob.WaitForExit(TimeSpan.FromSeconds(5)).ShouldBeTrue("задание закрыто — его процесс снят");
            outside.HasExited.ShouldBeFalse("процесс вне задания живет");
        }
        finally
        {
            foreach (var process in new[] { inJob, outside }.Where(p => !p.HasExited))
            {
                process.Kill();
            }
        }
    }

    private static Process Start(string ping)
    {
        var start = new ProcessStartInfo(ping) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-n");
        start.ArgumentList.Add("30");
        start.ArgumentList.Add("127.0.0.1");
        return Process.Start(start)!;
    }
}
