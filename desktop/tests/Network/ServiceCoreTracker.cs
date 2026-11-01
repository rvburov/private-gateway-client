using System.Globalization;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Service;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Последние ядра стенда — из событий запуска, а не хвоста журнала: предупреждения вправе вытеснить даже свежую
/// информационную запись. Память ограничена одним PID прокси и одной неизменяемой парой PID туннеля.
/// </summary>
internal sealed class ServiceCoreTracker : IDisposable
{
    private const string ProxyPrefix = "Ядро Xray запущено, процесс ";
    private const string TunnelPrefix = "Туннель поднят: Xray — процесс ";
    private const string TunnelMarker = ", sing-box — процесс ";

    private readonly ServiceJournal _journal;
    private int _proxy;
    private TunnelProcesses? _tunnel;

    public ServiceCoreTracker(ServiceJournal journal)
    {
        _journal = journal;
        _journal.Added += Observe;
    }

    /// <summary>Последнее запущенное ядро прокси: при переподключении остается прежним до запуска нового.</summary>
    public int Proxy => Volatile.Read(ref _proxy) is > 0 and var pid ? pid : throw new InvalidOperationException("Служба еще не запустила Xray");

    /// <summary>Пара одного запуска туннеля: читатель не получит PID от разных запусков.</summary>
    public (int Xray, int SingBox) Tunnel
    {
        get
        {
            var snapshot = Volatile.Read(ref _tunnel) ?? throw new InvalidOperationException("Служба еще не запустила туннель");
            return (snapshot.Xray, snapshot.SingBox);
        }
    }

    public void Dispose() => _journal.Added -= Observe;

    private void Observe(object? sender, JournalEntry entry)
    {
        if (entry.Level != JournalLevel.Info || entry.Source != "service")
        {
            return;
        }

        if (entry.Message.StartsWith(ProxyPrefix, StringComparison.Ordinal))
        {
            var text = entry.Message.AsSpan(ProxyPrefix.Length);
            var comma = text.IndexOf(',');
            if (TryPid(comma < 0 ? text : text[..comma], out var pid))
            {
                Volatile.Write(ref _proxy, pid);
            }

            return;
        }

        if (entry.Message.StartsWith(TunnelPrefix, StringComparison.Ordinal))
        {
            var text = entry.Message.AsSpan(TunnelPrefix.Length);
            var at = text.IndexOf(TunnelMarker, StringComparison.Ordinal);
            if (at > 0 && TryPid(text[..at], out var xray) && TryPid(text[(at + TunnelMarker.Length)..], out var singBox))
            {
                Volatile.Write(ref _tunnel, new TunnelProcesses(xray, singBox));
            }
        }
    }

    private static bool TryPid(ReadOnlySpan<char> value, out int pid) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out pid) && pid > 0;

    private sealed record TunnelProcesses(int Xray, int SingBox);
}
