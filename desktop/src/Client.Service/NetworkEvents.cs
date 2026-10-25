using System.Net;
using System.Net.NetworkInformation;
using PrivateGateway.Client.Network.SingBox;

namespace PrivateGateway.Client.Service;

/// <summary>Сеть машины сменилась: другой Wi-Fi, точка доступа, кабель, новый адрес, машина проснулась.</summary>
public interface INetworkEvents
{
    event EventHandler? Changed;
}

/// <summary>
/// События ОС (<see cref="NetworkChange"/>), отобранные по делу: ОС сообщает о любой перемене, в том
/// числе о своем туннеле, который поднимает служба. Поэтому после затишья в 2 с сравнивается отпечаток
/// сети — включенные адаптеры с адресами, кроме петли и своего туннеля; не изменился — события нет.
/// </summary>
public sealed class SystemNetworkEvents : INetworkEvents, IDisposable
{
    /// <summary>Сколько ждать, пока ОС закончит сообщать об одной перемене.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);

    private readonly TimeProvider _time;
    private readonly Func<string> _current;
    private readonly Lock _gate = new();
    private string _fingerprint;
    private ITimer? _timer;

    /// <param name="time">Часы затишья.</param>
    /// <param name="fingerprint">Отпечаток сети сейчас (тесты); null — адаптеры машины.</param>
    public SystemNetworkEvents(TimeProvider time, Func<string>? fingerprint = null)
    {
        _time = time;
        _current = fingerprint ?? Current;
        _fingerprint = _current();
        NetworkChange.NetworkAddressChanged += OnSystemChange;
        NetworkChange.NetworkAvailabilityChanged += OnSystemChange;
    }

    public event EventHandler? Changed;

    /// <summary>
    /// Отпечаток сети: включенные адаптеры, кроме петли и <paramref name="ownAdapter"/>, с их адресами — по
    /// порядку имен и адресов, чтобы тот же набор давал ту же строку.
    /// </summary>
    public static string Fingerprint(IEnumerable<(string Name, bool IsLoopback, IEnumerable<IPAddress> Addresses)> adapters, string? ownAdapter)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        return string.Join('\n', adapters
            .Where(a => !a.IsLoopback && !string.Equals(a.Name, ownAdapter, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Name + "=" + string.Join(',', a.Addresses.Select(x => x.ToString()).Order(StringComparer.Ordinal)))
            .Order(StringComparer.Ordinal));
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnSystemChange;
        NetworkChange.NetworkAvailabilityChanged -= OnSystemChange;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    private static string Current() => Fingerprint(
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .Select(n => (n.Name, n.NetworkInterfaceType == NetworkInterfaceType.Loopback,
                n.GetIPProperties().UnicastAddresses.Select(u => u.Address))),
        SingBoxConfigBuilder.InterfaceName);

    /// <summary>ОС сообщила о перемене: отпечаток сравнить, когда она 2 с ни о чем не сообщает.</summary>
    public void Notify()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = _time.CreateTimer(_ => Settled(), null, Quiet, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnSystemChange(object? sender, EventArgs e) => Notify();

    private void Settled()
    {
        var now = _current();
        lock (_gate)
        {
            if (now == _fingerprint)
            {
                return;
            }

            _fingerprint = now;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
