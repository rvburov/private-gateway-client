using System.Diagnostics;
using System.Text.RegularExpressions;
using PrivateGateway.Client.Contracts;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Service;

/// <summary>Сведения о службе для GetServiceInfo и путь к ядру по умолчанию.</summary>
public static partial class ServiceHost
{
    /// <summary>Ядро рядом со службой: cores/xray/xray(.exe); нет — null, подключение откажет с PGC-SVC-002.</summary>
    public static string? DefaultXray()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "cores", "xray", OperatingSystem.IsWindows() ? "xray.exe" : "xray");
        return File.Exists(path) ? path : null;
    }

    public static V1.ServiceInfo Info(string? xray)
    {
        var info = new V1.ServiceInfo
        {
            ServiceVersion = ServiceVersion.Current,
            ApiVersion = ContractMapping.ApiVersion,
            Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        };
        var version = xray is not null && File.Exists(xray) ? XrayVersion(xray) : null;
        info.Cores.Add(new V1.CoreInfo { Name = "xray", Version = version ?? string.Empty, Available = version is not null });
        return info;
    }

    /// <summary>«Xray 25.8.3 (…)» → «25.8.3»; не запустилось — null.</summary>
    private static string? XrayVersion(string xray)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(xray)
            {
                ArgumentList = { "version" },
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return Version().Match(output) is { Success: true } match ? match.Groups[1].Value : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    [GeneratedRegex(@"Xray (\d+\.\d+\.\d+)")]
    private static partial Regex Version();
}
