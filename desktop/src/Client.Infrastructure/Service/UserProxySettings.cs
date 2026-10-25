using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using PrivateGateway.Client.Application.Connection;

namespace PrivateGateway.Client.Infrastructure.Service;

/// <summary>
/// Системный прокси пользователя окна. Установленная служба работает от SYSTEM или root и настроек
/// пользователя не видит — их читает окно, и только читает: Windows — параметры Интернета (HKCU), Linux —
/// KDE (kioslaverc) или GNOME (gsettings), macOS — scutil. Разбор — чистыми функциями, их тесты идут на
/// любой ОС.
/// </summary>
/// <param name="read">Чтение настроек; ошибка чтения — прокси не показывается.</param>
public sealed class UserProxySettings(Func<UserProxy?> read) : IUserProxySettings
{
    private const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string Gnome = "org.gnome.system.proxy";

    // По порядку предпочтения: прокси для веба, потом SOCKS.
    private static readonly string[] GnomeProtocols = ["http", "https", "socks"];
    private static readonly string[] KdeKeys = ["httpProxy", "httpsProxy", "socksProxy"];
    private static readonly string[] MacProtocols = ["HTTP", "HTTPS", "SOCKS"];

    /// <summary>Настройки пользователя, от имени которого работает окно.</summary>
    public static UserProxySettings ForCurrentUser() => new(() =>
        OperatingSystem.IsWindows() ? Windows()
        : OperatingSystem.IsMacOS() ? FromScutil(Run("/usr/sbin/scutil", "--proxy"))
        : (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty).Contains("KDE", StringComparison.OrdinalIgnoreCase)
            ? FromKde(Kioslaverc())
            : FromGnome((schema, key) => Run("gsettings", "get", schema, key)));

    public UserProxy? Read()
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Настройки не прочитать — прокси не показывается: проверка не выдумывает.
            return null;
        }
    }

    /// <summary>
    /// Windows: скрипт автонастройки (AutoConfigURL) — раньше ручного прокси, как и у самой Windows; ручной —
    /// ProxyEnable и ProxyServer «узел:порт» или «http=узел:порт;https=…» (берется http, иначе первый).
    /// </summary>
    public static UserProxy? FromWindows(int? enabled, string? server, string? script)
    {
        if (!string.IsNullOrWhiteSpace(script))
        {
            return new UserProxy(script.Trim(), IsScript: true);
        }

        if (enabled != 1 || string.IsNullOrWhiteSpace(server))
        {
            return null;
        }

        var parts = server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var http = parts.FirstOrDefault(p => p.StartsWith("http=", StringComparison.OrdinalIgnoreCase)) ?? parts[0];
        return new UserProxy(http[(http.IndexOf('=', StringComparison.Ordinal) + 1)..]);
    }

    /// <summary>
    /// GNOME: «manual» — http, иначе https, иначе socks; «auto» — скрипт (autoconfig-url, пустой — WPAD).
    /// <paramref name="get"/> — значение, как его печатает gsettings (строки в кавычках); нет его — null.
    /// </summary>
    public static UserProxy? FromGnome(Func<string, string, string?> get)
    {
        ArgumentNullException.ThrowIfNull(get);
        switch (Unquote(get(Gnome, "mode")))
        {
            case "manual":
                foreach (var protocol in GnomeProtocols)
                {
                    if (Unquote(get($"{Gnome}.{protocol}", "host")) is { Length: > 0 } host
                        && int.TryParse(get($"{Gnome}.{protocol}", "port")?.Trim(), CultureInfo.InvariantCulture, out var port) && port > 0)
                    {
                        return new UserProxy($"{host}:{port.ToString(CultureInfo.InvariantCulture)}");
                    }
                }

                return null;
            case "auto":
                return new UserProxy(Unquote(get(Gnome, "autoconfig-url")) is { Length: > 0 } url ? url : "WPAD", IsScript: true);
            default:
                return null;
        }
    }

    /// <summary>
    /// KDE (раздел «Proxy Settings» в kioslaverc): ProxyType 1 — вручную (http, https, socks; адрес — «схема://узел
    /// порт» или «схема://узел:порт»), 2 — скрипт, 3 — WPAD; 0 и 4 (переменные окружения) — нет.
    /// </summary>
    public static UserProxy? FromKde(string? kioslaverc)
    {
        if (kioslaverc is null)
        {
            return null;
        }

        var values = Section(kioslaverc, "Proxy Settings");
        return values.GetValueOrDefault("ProxyType") switch
        {
            "1" => KdeKeys.Select(k => KdeAddress(values.GetValueOrDefault(k))).FirstOrDefault(a => a is not null)
                is { } address ? new UserProxy(address) : null,
            "2" => new UserProxy(values.GetValueOrDefault("Proxy Config Script") is { Length: > 0 } script ? script : "PAC", IsScript: true),
            "3" => new UserProxy("WPAD", IsScript: true),
            _ => null,
        };
    }

    /// <summary>macOS (<c>scutil --proxy</c>): скрипт и WPAD — раньше ручного, как у самой macOS; вручную — HTTP, HTTPS, SOCKS.</summary>
    public static UserProxy? FromScutil(string? output)
    {
        if (output is null)
        {
            return null;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in output.Split('\n').Select(l => l.Split(" : ", 2, StringSplitOptions.TrimEntries)).Where(p => p.Length == 2))
        {
            values.TryAdd(pair[0], pair[1]);
        }

        if (values.GetValueOrDefault("ProxyAutoConfigEnable") == "1")
        {
            return new UserProxy(values.GetValueOrDefault("ProxyAutoConfigURLString") is { Length: > 0 } url ? url : "PAC", IsScript: true);
        }

        if (values.GetValueOrDefault("ProxyAutoDiscoveryEnable") == "1")
        {
            return new UserProxy("WPAD", IsScript: true);
        }

        return MacProtocols
            .Where(p => values.GetValueOrDefault(p + "Enable") == "1" && !string.IsNullOrEmpty(values.GetValueOrDefault(p + "Proxy")))
            .Select(p => new UserProxy(values[p + "Proxy"] + ":" + values.GetValueOrDefault(p + "Port")))
            .FirstOrDefault();
    }

    [SupportedOSPlatform("windows")]
    private static UserProxy? Windows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(InternetSettings);
        return key is null ? null : FromWindows(key.GetValue("ProxyEnable") as int?, key.GetValue("ProxyServer") as string, key.GetValue("AutoConfigURL") as string);
    }

    private static string? Kioslaverc()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        var path = Path.Combine(config, "kioslaverc");
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    /// <summary>Ключи раздела INI; ключи с пометками KDE («ключ[$e]») — без пометок.</summary>
    private static Dictionary<string, string> Section(string ini, string name)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var inside = false;
        foreach (var line in ini.Split('\n').Select(l => l.Trim()))
        {
            if (line.StartsWith('['))
            {
                inside = line == $"[{name}]";
            }
            else if (inside && line.IndexOf('=', StringComparison.Ordinal) is > 0 and var at)
            {
                var key = line[..at].Trim();
                values[key.Split('[')[0]] = line[(at + 1)..].Trim();
            }
        }

        return values;
    }

    /// <summary>«http://узел порт», «http://узел:порт/» → «узел:порт»; пусто — null.</summary>
    private static string? KdeAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var address = value.Contains("://", StringComparison.Ordinal) ? value[(value.IndexOf("://", StringComparison.Ordinal) + 3)..] : value;
        address = address.TrimEnd('/').Replace(' ', ':');
        return address.Length > 0 ? address : null;
    }

    private static string? Unquote(string? value) => value?.Trim().Trim('\'');

    /// <summary>Вывод программы; нет ее или она вышла с ошибкой — null.</summary>
    private static string? Run(string program, params string[] arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
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

            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            error.GetAwaiter().GetResult();
            return process.ExitCode == 0 ? output : null;
        }
        catch (Win32Exception)
        {
            // Программы нет — значит, и настроек этого окружения нет.
            return null;
        }
    }
}
