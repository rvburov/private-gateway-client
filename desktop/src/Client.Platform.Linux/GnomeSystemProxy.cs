using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Platform.Linux;

/// <summary>
/// Системный прокси GNOME (gsettings, org.gnome.system.proxy): им следуют Chrome, Firefox с «системным
/// прокси» и программы GNOME. Локальные адреса и частные сети — напрямую. Значения в снимке — как их
/// печатает gsettings (GVariant), так же они и возвращаются. KDE — <see cref="KdeSystemProxy"/>.
/// </summary>
/// <param name="gsettings">Вызов gsettings: аргументы → вывод; null — не вышло.</param>
public sealed class GnomeSystemProxy(Func<IReadOnlyList<string>, string?> gsettings) : ISystemProxy
{
    private const string Proxy = "org.gnome.system.proxy";
    private static readonly string[] Protocols = [Proxy + ".http", Proxy + ".https", Proxy + ".socks"];

    // Режим — последним: включается и выключается прокси, когда адреса уже на месте.
    private static readonly (string Schema, string Key)[] Keys =
        [.. Protocols.SelectMany(p => new[] { (p, "host"), (p, "port") }), (Proxy, "ignore-hosts"), (Proxy, "mode")];

    /// <summary>Мимо прокси: локальные имена и адреса, частные сети.</summary>
    public const string Bypass = "['localhost', '127.0.0.0/8', '::1', '10.0.0.0/8', '172.16.0.0/12', '192.168.0.0/16']";

    public string Name => "GNOME";

    /// <summary>Есть ли настройки прокси GNOME в этом сеансе.</summary>
    public bool IsAvailable => gsettings(["get", Proxy, "mode"]) is not null;

    /// <summary>Настройки GNOME этого сеанса через gsettings; нет их — null.</summary>
    public static GnomeSystemProxy? ForThisSession() => new GnomeSystemProxy(Run) is { IsAvailable: true } proxy ? proxy : null;

    public SystemProxySnapshot Read() =>
        new(Keys.ToDictionary(k => k.Schema + " " + k.Key, k => gsettings(["get", k.Schema, k.Key])?.Trim()));

    public void Enable(int port)
    {
        foreach (var protocol in Protocols)
        {
            Set(protocol, "host", "'127.0.0.1'");
            Set(protocol, "port", port.ToString(CultureInfo.InvariantCulture));
        }

        Set(Proxy, "ignore-hosts", Bypass);
        Set(Proxy, "mode", "'manual'");
    }

    public void Restore(SystemProxySnapshot previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        foreach (var (schema, key) in Keys)
        {
            if (previous.Values.GetValueOrDefault(schema + " " + key) is { } value)
            {
                Set(schema, key, value);
            }
        }
    }

    private void Set(string schema, string key, string value) =>
        _ = gsettings(["set", schema, key, value]) ?? throw new IOException($"gsettings не записал {schema} {key}");

    private static string? Run(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("gsettings") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            return process.WaitForExit(5000) && process.ExitCode == 0 ? output : null;
        }
        catch (Win32Exception)
        {
            // gsettings нет — не GNOME.
            return null;
        }
    }
}
