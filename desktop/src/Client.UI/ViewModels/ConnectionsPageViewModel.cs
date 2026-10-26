using System.Collections.ObjectModel;
using System.Net;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core.Inspector;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// «Соединения и трафик» — сетевой инспектор (этап 7, экрана в макете нет — по дизайн-системе): скорость и объем трафика
/// адаптеров (счетчики ОС раз в секунду, пока страница открыта) и сокеты машины с процессами — по возможностям ОС. Читает
/// служба; адреса видны только на экране — в журнал не пишутся. Подстраница «Проверки».
/// </summary>
public sealed partial class ConnectionsPageViewModel : ObservableObject, IDisposable
{
    /// <summary>Сколько строк сокетов показывать: на занятой машине их тысячи.</summary>
    public const int MaxRows = 300;

    private readonly IGatewayClient _gateway;
    private readonly INavigator _navigator;
    private CancellationTokenSource? _watching;
    private IReadOnlyList<NetworkConnection>? _connections;
    private TrafficSample? _previous;
    private TrafficSample? _latest;

    public ConnectionsPageViewModel(IGatewayClient gateway, INavigator navigator)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        _gateway = gateway;
        _navigator = navigator;
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            ShowTraffic();
            ShowConnections();
            OnPropertyChanged(string.Empty);
        };
    }

    public ObservableCollection<TrafficRowViewModel> Traffic { get; } = [];

    public ObservableCollection<ConnectionRowViewModel> Connections { get; } = [];

    public bool HasTraffic => Traffic.Count > 0;

    /// <summary>Сокеты, которые ждут входящих (слушают), а не активные соединения.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    public partial bool IsListening { get; set; }

    public bool IsActive
    {
        get => !IsListening;
        set => IsListening = !value;
    }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool IsUnavailable { get; private set; }

    /// <summary>«Показано 300 из 1 204» или «Соединений: 42»; пусто — пока не прочитано.</summary>
    [ObservableProperty]
    public partial string CountText { get; private set; } = string.Empty;

    /// <summary>Есть сокеты без владельца — у ОС на это нет прав (служба пользователя видит только свои процессы).</summary>
    [ObservableProperty]
    public partial bool HasHiddenOwners { get; private set; }

    partial void OnIsListeningChanged(bool value) => ShowConnections();

    /// <summary>Страница на экране: сокеты заново, счетчики — раз в секунду, пока не <see cref="Stop"/>.</summary>
    public void Start()
    {
        _ = RefreshAsync();
        if (_watching is null)
        {
            _watching = new CancellationTokenSource();
            _ = WatchAsync(_watching.Token);
        }
    }

    public void Dispose() => Stop();

    /// <summary>Страница ушла с экрана — счетчики больше не нужны.</summary>
    public void Stop()
    {
        _watching?.Cancel();
        _watching?.Dispose();
        _watching = null;
        (_previous, _latest) = (null, null);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var connections = await _gateway.ListConnectionsAsync().ConfigureAwait(true);
            IsUnavailable = connections is null;
            _connections = connections ?? _connections;
            ShowConnections();
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void Back() => _navigator.Navigate(Pages.Check);

    // Счетчики раз в секунду, пока страница жива: скорость — по двум соседним замерам.
    private async Task WatchAsync(CancellationToken stop)
    {
        try
        {
            await foreach (var sample in _gateway.WatchTrafficAsync(stop).ConfigureAwait(false))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!stop.IsCancellationRequested)
                    {
                        (_previous, _latest) = (_latest, sample);
                        ShowTraffic();
                    }
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Страница закрыта.
        }
    }

    private void ShowTraffic()
    {
        Traffic.Clear();
        if (_latest is not { } latest)
        {
            OnPropertyChanged(nameof(HasTraffic));
            return;
        }

        var seconds = _previous is { } previous ? (latest.TakenAt - previous.TakenAt).TotalSeconds : 0;
        foreach (var adapter in latest.Adapters)
        {
            var before = _previous?.Adapters.FirstOrDefault(a => a.Name == adapter.Name);
            Traffic.Add(new TrafficRowViewModel(adapter,
                before is not null && seconds > 0 ? Math.Max(0, (adapter.Received - before.Received) / seconds) : null,
                before is not null && seconds > 0 ? Math.Max(0, (adapter.Sent - before.Sent) / seconds) : null));
        }

        OnPropertyChanged(nameof(HasTraffic));
    }

    private void ShowConnections()
    {
        Connections.Clear();
        if (_connections is not { } all)
        {
            CountText = string.Empty;
            return;
        }

        var shown = all.Where(c => Waits(c.Socket) == IsListening)
            .OrderBy(c => c.Process is null)
            .ThenBy(c => c.Process, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(c => c.Socket.Remote?.ToString() ?? c.Socket.Local.ToString(), StringComparer.Ordinal)
            .ToList();
        foreach (var connection in shown.Take(MaxRows))
        {
            Connections.Add(new ConnectionRowViewModel(connection));
        }

        CountText = shown.Count > MaxRows
            ? Loc.Instance.Format("conn.count.limited", MaxRows, shown.Count)
            : Loc.Instance.Format("conn.count", shown.Count);
        HasHiddenOwners = all.Any(c => c.Socket.ProcessId is null);
    }

    /// <summary>Сокет ждет входящих: TCP слушает, UDP ни с кем не связан.</summary>
    public static bool Waits(SocketEntry socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        return socket.State == SocketState.Listen || socket is { State: SocketState.Open, Remote: null };
    }

    /// <summary>«1,2 МБ/с», «830 КБ/с», «12 Б/с».</summary>
    public static string Rate(double bytesPerSecond) => Loc.Instance.Format("conn.rate", Size(bytesPerSecond));

    /// <summary>«1,2 ГБ», «830 КБ», «12 Б» — по 1024.</summary>
    public static string Size(double bytes)
    {
        string[] units = ["conn.unit.b", "conn.unit.kb", "conn.unit.mb", "conn.unit.gb", "conn.unit.tb"];
        var unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return Loc.Instance.Format(units[unit], bytes.ToString(unit == 0 || bytes >= 100 ? "0" : "0.#", Loc.Instance.Culture));
    }
}

/// <summary>Адаптер: скорость приема и отправки сейчас и сколько всего с его включения.</summary>
public sealed class TrafficRowViewModel(AdapterCounters adapter, double? down, double? up)
{
    public string Name { get; } = adapter.Name;

    /// <summary>«↓ 1,2 МБ/с · ↑ 80 КБ/с»; первый замер — без скорости.</summary>
    public string Speed { get; } = down is { } d && up is { } u
        ? Loc.Instance.Format("conn.speed", ConnectionsPageViewModel.Rate(d), ConnectionsPageViewModel.Rate(u))
        : Loc.Instance["conn.speed.measuring"];

    /// <summary>«принято 1,2 ГБ · отправлено 230 МБ».</summary>
    public string Total { get; } = Loc.Instance.Format("conn.total", ConnectionsPageViewModel.Size(adapter.Received), ConnectionsPageViewModel.Size(adapter.Sent));
}

/// <summary>Сокет: процесс, протокол и состояние, свой адрес и адрес другой стороны.</summary>
public sealed class ConnectionRowViewModel(NetworkConnection connection)
{
    public string Process { get; } = connection.Process is { } name
        ? connection.Socket.ProcessId is { } pid ? $"{name} · {pid}" : name
        : connection.Socket.ProcessId is { } id ? Loc.Instance.Format("conn.process.pid", id) : Loc.Instance["conn.process.unknown"];

    /// <summary>«TCP · установлено».</summary>
    public string Kind { get; } = $"{connection.Socket.Protocol.ToString().ToUpperInvariant()} · {Loc.Instance["conn.state." + connection.Socket.State.ToString().ToLowerInvariant()]}";

    /// <summary>«192.0.2.23:51234 → 203.0.113.10:443» или свой адрес у слушающих.</summary>
    public string Endpoints { get; } = connection.Socket.Remote is { } remote
        ? $"{Format(connection.Socket.Local)} → {Format(remote)}"
        : Format(connection.Socket.Local);

    private static string Format(IPEndPoint endpoint) => endpoint.Address.Equals(IPAddress.Any) || endpoint.Address.Equals(IPAddress.IPv6Any)
        ? Loc.Instance.Format("conn.anyAddress", endpoint.Port)
        : endpoint.ToString();
}
