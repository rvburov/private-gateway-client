using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Platform.MacOS;

/// <summary>
/// Блок при обрыве на macOS — правила pf в своем якоре <see cref="Anchor"/>. Штатный /etc/pf.conf включает все якоря
/// <c>com.apple/*</c>, поэтому основные правила машины не меняются: якорь загружается и снимается целиком, его правила с
/// <c>quick</c> решают первыми — пропускают туннель, сервер, петлю, DHCP и разрешенную локальную сеть и закрывают
/// остальное, в том числе пересылаемое (общий доступ к интернету). Замена правил — одной загрузкой якоря
/// (<c>pfctl -a … -f -</c>): щели между старыми и новыми нет. pf включается своей ссылкой (<c>pfctl -E</c>, токен в
/// файле) и ею же отпускается — pf, включенный другой программой, остается включенным.
/// </summary>
/// <param name="pfctl">Вызов pfctl: аргументы и stdin → код выхода, вывод и текст ошибок.</param>
/// <param name="tokenFile">Где помнить ссылку на включенный pf (переживает перезапуск службы).</param>
/// <param name="anchor">Якорь; тесты дают свой, на который основные правила не ссылаются.</param>
public sealed partial class PfKillSwitch(
    Func<IReadOnlyList<string>, string?, (int Code, string Output, string Error)> pfctl,
    string tokenFile = "/var/run/private-gateway/pf.token",
    string anchor = PfKillSwitch.Anchor) : IKillSwitch
{
    /// <summary>Якорь блока: под <c>com.apple/*</c>, который вызывает штатный /etc/pf.conf.</summary>
    public const string Anchor = "com.apple/250.PrivateGateway";

    private const string Program = "/sbin/pfctl";

    public string Name => "pf";

    /// <summary>Раздельный режим: обход sing-box на macOS по программе не узнать — он закрыт блоком.</summary>
    public string? Warning { get; private set; }

    public bool IsEnabled => pfctl(["-a", anchor, "-s", "rules"], null) is { Code: 0 } rules && rules.Output.Trim().Length > 0;

    /// <summary>
    /// Правила якоря на месте, pf включен, а основные правила все еще вызывают якоря <c>com.apple/*</c>: выключенный pf
    /// или свой pf.conf без этой строки — и правила якоря не действуют.
    /// </summary>
    public bool IsIntact => IsEnabled
        && pfctl(["-s", "info"], null).Output.Contains("Status: Enabled", StringComparison.Ordinal)
        && pfctl(["-s", "rules"], null).Output.Contains("anchor \"com.apple/*\"", StringComparison.Ordinal);

    /// <summary>pfctl этой машины; не macOS — null.</summary>
    public static PfKillSwitch? ForThisMachine() =>
        OperatingSystem.IsMacOS() && File.Exists(Program) ? new PfKillSwitch(Run) : null;

    public void Enable(KillSwitchPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Check(["-a", anchor, "-f", "-"], Ruleset(policy));
        Warning = policy.Direct is null ? null : "раздельный режим: на macOS обход туннеля закрыт блоком при обрыве";
        if (File.Exists(tokenFile))
        {
            return;
        }

        // pfctl -E включает pf и выдает ссылку «Token : <число>»: pf выключится, только когда отпустят все ссылки.
        var (code, output, error) = pfctl(["-E"], null);
        if (code != 0 || TokenPattern().Match(output + "\n" + error) is not { Success: true } token)
        {
            throw new IOException($"pfctl -E: {code} {error}".Trim());
        }

        Directory.CreateDirectory(Path.GetDirectoryName(tokenFile)!);
        File.WriteAllText(tokenFile, token.Groups[1].Value);
    }

    public void Disable()
    {
        Check(["-a", anchor, "-F", "all"], null);
        if (!File.Exists(tokenFile))
        {
            return;
        }

        // Ссылки уже нет (перезагрузка) — отпускать нечего: ошибку pfctl -X не считаем.
        _ = pfctl(["-X", File.ReadAllText(tokenFile).Trim()], null);
        File.Delete(tokenFile);
    }

    /// <summary>Правила якоря для <c>pfctl -a … -f -</c>: решает первое подходящее (<c>quick</c>), остальное закрыто.</summary>
    public static string Ruleset(KillSwitchPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var rules = new List<string> { "pass out quick on lo0 all" };
        if (policy.TunnelInterface is { } tunnel)
        {
            // Имя попадает в текст правил — только буквы, цифры, «_», «.», «-» и не длиннее 15 знаков.
            if (tunnel.Length is 0 or > 15 || !tunnel.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-'))
            {
                throw new ArgumentException("Недопустимое имя адаптера туннеля: " + tunnel, nameof(policy));
            }

            rules.Add($"pass out quick on {tunnel} all");
        }

        foreach (var server in policy.Servers)
        {
            if (server.Port is <= 0 or > 65535)
            {
                throw new ArgumentException("Недопустимый порт сервера: " + server.Port.ToString(CultureInfo.InvariantCulture), nameof(policy));
            }

            rules.Add($"pass out quick {Family(server.Address)} proto {{ tcp, udp }} from any to {server.Address} port {server.Port.ToString(CultureInfo.InvariantCulture)}");
        }

        // Поиск адреса сервера службой под блоком: только сокеты root (служба) и только к DoH.
        if (policy.Lookup is { } lookup)
        {
            foreach (var endpoint in lookup.Endpoints)
            {
                rules.Add($"pass out quick {Family(endpoint.Address)} proto tcp from any to {endpoint.Address} port {endpoint.Port.ToString(CultureInfo.InvariantCulture)} user root");
            }
        }

        // Свой DNS в частной сети: его выбрал пользователь, и Xray спрашивает его напрямую — раньше закрытия DNS.
        if (policy.LanDns is { } lanDns)
        {
            rules.Add($"pass out quick {Family(lanDns)} proto {{ tcp, udp }} from any to {lanDns} port 53");
        }

        // Адрес от DHCP и соседи IPv6: без них машина теряет сеть, данные через них наружу не уходят.
        rules.Add("pass out quick inet proto udp from any port 68 to any port 67");
        rules.Add("pass out quick inet proto udp from any port 67 to { 255.255.255.255, 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16 } port 68");
        rules.Add("pass out quick inet6 proto udp from any port 546 to any port 547");
        rules.Add("pass out quick inet6 proto icmp6 all icmp6-type { routersol, neighbrsol, neighbradv }");

        if (policy.BlockOutsideDns)
        {
            // DNS роутера мимо туннеля — утечка, даже если локальная сеть разрешена; LLMNR и NetBIOS — имена во всю сеть.
            // TCP — отказом (сброс): программа узнает сразу, а не через повторы SYN.
            rules.Add("block return out quick proto tcp from any to any port { 53, 137, 138, 853, 5355 }");
            rules.Add("block drop out quick proto udp from any to any port { 53, 137, 138, 853, 5355 }");
        }

        // «Блокировать IPv6 вне туннеля» выключено при выключенном блоке: IPv6 мимо туннеля — как выбрал пользователь.
        if (policy.Ipv6Outside)
        {
            rules.Add("pass out quick inet6 all");
        }

        if (policy.AllowLan)
        {
            rules.Add("pass out quick inet from any to { 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16, 169.254.0.0/16 }");
            rules.Add("pass out quick inet6 from any to { fc00::/7, fe80::/10 }");
            if (policy.LanDiscovery)
            {
                rules.Add("pass out quick inet from any to { 224.0.0.0/4, 255.255.255.255 }");
                rules.Add("pass out quick inet6 from any to ff00::/8");
            }
        }

        // Остальное закрыто: TCP — отказом (сброс), иначе соединение мимо туннеля ждало бы повторов SYN.
        rules.Add("block return out quick proto tcp all");
        rules.Add("block drop out quick all");
        return string.Join('\n', rules) + "\n";
    }

    private static string Family(System.Net.IPAddress address) => address.AddressFamily == AddressFamily.InterNetworkV6 ? "inet6" : "inet";

    private void Check(IReadOnlyList<string> arguments, string? input)
    {
        var (code, _, error) = pfctl(arguments, input);
        if (code != 0)
        {
            throw new IOException($"pfctl {string.Join(' ', arguments)}: {code} {error}".Trim());
        }
    }

    [GeneratedRegex(@"Token\s*:\s*(\d+)")]
    private static partial Regex TokenPattern();

    private static (int Code, string Output, string Error) Run(IReadOnlyList<string> arguments, string? input)
    {
        var start = new ProcessStartInfo(Program)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start) ?? throw new IOException("pfctl не запустился");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(input ?? string.Empty);
            process.StandardInput.Close();
            process.WaitForExit();
            return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult().Trim());
        }
        catch (Win32Exception e)
        {
            throw new IOException("pfctl не запустился: " + e.Message, e);
        }
    }
}
