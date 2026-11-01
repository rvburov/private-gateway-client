using System.Net.Sockets;

namespace PrivateGateway.Client.Service.Cores;

/// <summary>Один локальный порт, удерживаемый слушателем TCP и сокетом UDP до освобождения этой пары.</summary>
public sealed class LocalPortLease : IDisposable
{
    private readonly Socket _first;
    private readonly Socket _second;

    internal LocalPortLease(int port, Socket first, Socket second)
    {
        Port = port;
        _first = first;
        _second = second;
    }

    /// <summary>Номер удерживаемого порта. После Dispose номер остается известен, но сокеты его больше не удерживают.</summary>
    public int Port { get; }

    /// <summary>Освободить оба сокета. Повторный вызов безопасен.</summary>
    public void Dispose()
    {
        _second.Dispose();
        _first.Dispose();
        GC.SuppressFinalize(this);
    }
}
