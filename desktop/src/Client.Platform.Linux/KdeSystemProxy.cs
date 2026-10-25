using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Platform.Linux;

/// <summary>
/// Системный прокси KDE Plasma — раздел «Proxy Settings» файла kioslaverc: им следуют программы KDE, а в
/// Plasma и Chrome с Firefox при «системном прокси». Меняются только ключи прокси, остальной файл — как был;
/// запись — через временный файл, затем KDE просят перечитать настройки (D-Bus). В снимке — ключи и значения
/// файла как есть, с пометками KDE («ключ[$e]» — подставлять переменные); ключа не было — после возврата его
/// нет. Локальные адреса и частные сети — напрямую.
/// </summary>
/// <param name="path">Файл kioslaverc.</param>
/// <param name="notify">Попросить KDE перечитать настройки; null — не просить (тесты).</param>
public sealed class KdeSystemProxy(string path, Action? notify) : ISystemProxy
{
    private const string Section = "Proxy Settings";

    // Тип — последним: включается и выключается прокси, когда адреса уже на месте.
    private static readonly string[] Keys = ["httpProxy", "httpsProxy", "socksProxy", "NoProxyFor", "ProxyType"];

    /// <summary>Мимо прокси: локальные имена и адреса, частные сети.</summary>
    public const string Bypass = "localhost,127.0.0.0/8,::1,10.0.0.0/8,172.16.0.0/12,192.168.0.0/16";

    public string Name => "KDE";

    /// <summary>Сеанс Plasma (XDG_CURRENT_DESKTOP) — kioslaverc пользователя; другой сеанс — null.</summary>
    public static KdeSystemProxy? ForThisSession()
    {
        if (!(Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty).Contains("KDE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return new KdeSystemProxy(Path.Combine(config, "kioslaverc"), Reparse);
    }

    public SystemProxySnapshot Read()
    {
        var lines = File.Exists(path) ? File.ReadAllLines(path) : [];
        var (start, end) = Bounds(lines);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = start; i < end; i++)
        {
            if (Key(lines[i]) is { } key && Keys.Contains(key))
            {
                var at = lines[i].IndexOf('=', StringComparison.Ordinal);
                values[lines[i][..at].Trim()] = lines[i][(at + 1)..];
            }
        }

        return new SystemProxySnapshot(values);
    }

    public void Enable(int port)
    {
        var address = "127.0.0.1 " + port.ToString(CultureInfo.InvariantCulture);
        Write(new Dictionary<string, string?>
        {
            ["httpProxy"] = "http://" + address,
            ["httpsProxy"] = "http://" + address,
            ["socksProxy"] = "socks://" + address,
            ["NoProxyFor"] = Bypass,
            ["ProxyType"] = "1",
        });
    }

    public void Restore(SystemProxySnapshot previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        Write(new Dictionary<string, string?>(previous.Values, StringComparer.Ordinal));
    }

    /// <summary>
    /// Все ключи прокси раздела заменяются данными (ключ — с пометкой KDE или без; значение null — ключа нет) по
    /// порядку <see cref="Keys"/>; остальные строки файла не трогаются.
    /// </summary>
    private void Write(Dictionary<string, string?> values)
    {
        var lines = (File.Exists(path) ? File.ReadAllLines(path) : []).ToList();
        var (start, end) = Bounds([.. lines]);
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Length > 0)
            {
                lines.Add(string.Empty);
            }

            lines.Add($"[{Section}]");
            (start, end) = (lines.Count, lines.Count);
        }

        // Прежние строки ключей прокси (и с пометками KDE «ключ[$e]») — убрать, новые — в конец раздела.
        for (var i = end - 1; i >= start; i--)
        {
            if (Key(lines[i]) is { } key && Keys.Contains(key))
            {
                lines.RemoveAt(i);
                end--;
            }
        }

        // Пустые строки перед следующим разделом остаются после новых ключей.
        var at = end;
        while (at > start && lines[at - 1].Trim().Length == 0)
        {
            at--;
        }

        lines.InsertRange(at, Keys.SelectMany(k => values.Where(v => v.Value is not null && v.Key.Split('[')[0] == k).Select(v => $"{v.Key}={v.Value}")));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".pg-tmp";
        File.WriteAllText(temporary, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
        notify?.Invoke();
    }

    /// <summary>Строки раздела: от первой после заголовка до следующего заголовка; раздела нет — (-1, -1).</summary>
    private static (int Start, int End) Bounds(string[] lines)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() == $"[{Section}]")
            {
                var end = i + 1;
                while (end < lines.Length && !lines[end].TrimStart().StartsWith('['))
                {
                    end++;
                }

                return (i + 1, end);
            }
        }

        return (-1, -1);
    }

    /// <summary>Ключ строки «ключ=значение» без пометок KDE («ключ[$e]»); не ключ — null.</summary>
    private static string? Key(string line) =>
        line.IndexOf('=', StringComparison.Ordinal) is > 0 and var at ? line[..at].Trim().Split('[')[0] : null;

    /// <summary>KDE перечитывает настройки прокси (KIO 5 и 6); нет dbus-send — изменения подхватятся при следующем запуске программ.</summary>
    private static void Reparse()
    {
        foreach (var member in new[] { "reparseSlaveConfiguration", "reparseConfiguration" })
        {
            var start = new ProcessStartInfo("dbus-send") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var argument in new[] { "--type=signal", "/KIO/Scheduler", "org.kde.KIO.Scheduler." + member, "string:" })
            {
                start.ArgumentList.Add(argument);
            }

            try
            {
                using var process = Process.Start(start);
                process?.WaitForExit(5000);
            }
            catch (Win32Exception)
            {
                return;
            }
        }
    }
}
