using System.ComponentModel;
using System.Diagnostics;

namespace PrivateGateway.Client.Platform.Linux;

/// <summary>
/// Вызов ip: аргументы → вывод; не вышло — <see cref="IOException"/> с текстом ip. ip и nsenter — по
/// полному пути, без поиска в PATH: служба работает от root.
/// </summary>
public static class IpCommand
{
    private static readonly string[] Programs = ["/usr/sbin/ip", "/sbin/ip", "/usr/bin/ip", "/bin/ip"];
    private static readonly string[] Nsenter = ["/usr/bin/nsenter", "/usr/sbin/nsenter", "/bin/nsenter"];

    /// <summary>
    /// ip этой машины; <paramref name="netns"/> — внутри сетевого пространства (тесты: ядра режима VPN там же).
    /// Нет ip или nsenter — null.
    /// </summary>
    public static Func<IReadOnlyList<string>, string>? ForThisMachine(string? netns = null)
    {
        if (Programs.FirstOrDefault(File.Exists) is not { } ip)
        {
            return null;
        }

        if (netns is null)
        {
            return arguments => Run(ip, arguments);
        }

        return Nsenter.FirstOrDefault(File.Exists) is { } nsenter ? arguments => Run(nsenter, ["--net=" + netns, ip, .. arguments]) : null;
    }

    private static string Run(string program, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start) ?? throw new IOException("ip не запустился");
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : throw new IOException("ip: " + error.GetAwaiter().GetResult().Trim());
        }
        catch (Win32Exception e)
        {
            throw new IOException("ip не запустился: " + e.Message, e);
        }
    }
}
