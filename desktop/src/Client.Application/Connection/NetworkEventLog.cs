using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Lab;

namespace PrivateGateway.Client.Application.Connection;

/// <summary>
/// События сети с запуска окна (лаборатория, этап 8): каждый снимок состояния от службы сравнивается с прежним, разница —
/// события (<see cref="NetworkEvents"/>). Хранит последние <see cref="Capacity"/>, новые — в конце.
/// </summary>
public sealed class NetworkEventLog(IGatewayClient gateway, TimeProvider time)
{
    /// <summary>Сколько событий помнить.</summary>
    public const int Capacity = 50;

    private readonly Lock _gate = new();
    private readonly List<NetworkEvent> _events = [];
    private ConnectionStatus? _last;

    /// <summary>Новое событие; приходит в потоке, где служба прислала состояние.</summary>
    public event EventHandler<NetworkEvent>? Added;

    /// <summary>События по порядку — старые первыми.</summary>
    public IReadOnlyList<NetworkEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    /// <summary>Начать слушать службу (один раз, при запуске окна): состояние на этот момент — точка отсчета, а не событие.</summary>
    public void Start()
    {
        lock (_gate)
        {
            _last = gateway.Status;
        }

        gateway.StatusChanged += (_, status) => OnStatus(status);
    }

    private void OnStatus(ConnectionStatus status)
    {
        IReadOnlyList<NetworkEvent> added;
        lock (_gate)
        {
            added = NetworkEvents.Between(_last, status, time.GetUtcNow());
            _last = status;
            _events.AddRange(added);
            if (_events.Count > Capacity)
            {
                _events.RemoveRange(0, _events.Count - Capacity);
            }
        }

        foreach (var item in added)
        {
            Added?.Invoke(this, item);
        }
    }
}
