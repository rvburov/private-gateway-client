using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Service.Cores;

/// <summary>
/// Hysteria2 и TUIC за Xray (решение 452): своих выходов для них у Xray нет, поэтому выход сервера в его конфиге — SOCKS5 с
/// паролем на 127.0.0.1 (<see cref="RelayLink"/>), а за ним ретранслятор — sing-box отдельным процессом (GPL-3.0) с выходом
/// hysteria2 или tuic к серверу (<see cref="SingBoxConfigBuilder.BuildRelay"/>). Маршруты, DNS, FakeDNS и правила остаются
/// у Xray. Ретранслятор запускается раньше Xray и живет столько же (<see cref="CoreProcess.Attach"/>).
/// </summary>
public static class CoreRelay
{
    /// <summary>Вход ретранслятора на одно подключение: порт на 127.0.0.1, свободный для TCP и UDP, имя и пароль — по 128 случайных бит.</summary>
    public static RelayLink NewLink() => new(LocalPort.FreeForTcpAndUdp(), VpnTunnel.Secret(), VpnTunnel.Secret());

    /// <summary>
    /// Вход для сервера, которому нужен ретранслятор (<see cref="XrayConfigBuilder.NeedsRelay"/>), если есть сборка
    /// <paramref name="singBox"/>; остальным и без sing-box — null (Xray тогда за такой сервер не возьмется).
    /// </summary>
    public static RelayLink? LinkFor(ServerDefinition server, string? singBox) =>
        XrayConfigBuilder.NeedsRelay(server) && singBox is not null && File.Exists(singBox) ? NewLink() : null;

    /// <summary>
    /// Запустить Xray с конфигом <paramref name="config"/>; если есть <paramref name="relays"/> — сначала ретранслятор. Входы
    /// ретранслятор открывает не сразу: <paramref name="relayReady"/> — открыл ли (строка «sing-box started» — входы уже
    /// слушают; так и в сетевом пространстве тестов); ее ждут там же, где готовность Xray. Ретранслятор вышел — Xray снимается
    /// (<see cref="CoreProcess.Attach"/>). Нет sing-box — <see cref="TunnelException"/>, ничего не запускается.
    /// </summary>
    /// <param name="xray">Ядро Xray.</param>
    /// <param name="config">Конфиг Xray (<see cref="XrayConfigBuilder"/>).</param>
    /// <param name="singBox">sing-box для ретранслятора.</param>
    /// <param name="relays">Серверы за ретранслятором; пусто — только Xray.</param>
    /// <param name="log">«Журнал ядра»: строки ретранслятора ниже этого уровня в <paramref name="relayOutput"/> не идут.</param>
    /// <param name="relayReady">Ретранслятор открыл входы; без ретранслятора — всегда true.</param>
    /// <param name="netns">Сетевое пространство Linux (тесты); null — в системе.</param>
    /// <param name="xrayOutput">Вывод Xray с первой строки.</param>
    /// <param name="relayOutput">Вывод ретранслятора.</param>
    public static CoreProcess StartXray(string xray, JsonObject config, string? singBox, IReadOnlyList<RelayRoute> relays, CoreLogLevel log,
        out Func<bool> relayReady, string? netns = null, Action<string>? xrayOutput = null, Action<string>? relayOutput = null)
    {
        ArgumentNullException.ThrowIfNull(relays);
        if (relays.Count == 0)
        {
            relayReady = static () => true;
            return CoreProcess.StartXray(xray, config, netns, xrayOutput);
        }

        if (singBox is null || !File.Exists(singBox))
        {
            throw new TunnelException("Нет сборки sing-box — Hysteria2 и TUIC идут только через нее");
        }

        var started = new StrongBox<int>();
        var relay = CoreProcess.StartSingBox(singBox, SingBoxConfigBuilder.BuildRelay(relays, log), netns, line =>
        {
            if (line.Contains(SingBoxConfigBuilder.StartedLine, StringComparison.Ordinal))
            {
                Volatile.Write(ref started.Value, 1);
            }

            if (SingBoxConfigBuilder.ShownLine(line, log))
            {
                relayOutput?.Invoke(line);
            }
        });
        try
        {
            var core = CoreProcess.StartXray(xray, config, netns, xrayOutput);
            core.Attach(relay);
            relayReady = () => Volatile.Read(ref started.Value) == 1;
            return core;
        }
        catch
        {
            relay.Dispose();
            throw;
        }
    }
}
