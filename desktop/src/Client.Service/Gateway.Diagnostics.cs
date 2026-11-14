using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Network.SingBox;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Service;

public sealed partial class Gateway
{
    /// <summary>Снимок принадлежности проверки: счетчики трафика не меняют сессию, смена сети и подключения меняет.</summary>
    internal sealed record DiagnosticSnapshot(int Session, int NetworkLoss, CoreRequest? Request, ConnectionStatus Status);

    internal DiagnosticSnapshot CaptureDiagnosticSnapshot()
    {
        lock (_gate)
        {
            return new DiagnosticSnapshot(_session, Volatile.Read(ref _networkLoss), _request, Honest(_status));
        }
    }

    internal bool IsDiagnosticSnapshotCurrent(DiagnosticSnapshot snapshot)
    {
        lock (_gate)
        {
            var before = snapshot.Status;
            return snapshot.Session == _session && snapshot.NetworkLoss == Volatile.Read(ref _networkLoss)
                && ReferenceEquals(snapshot.Request, _request)
                && before.State == _status.State && before.Mode == _status.Mode && before.ServerId == _status.ServerId
                && before.Since == _status.Since && before.IsPaused == _status.IsPaused
                && before.NetworkChangedAt == _status.NetworkChangedAt && before.Network == _status.Network
                && before.NetworkUnavailable == _status.NetworkUnavailable;
        }
    }

    /// <summary>В проверке ошибку маршрутов обрабатывает отдельная строка и журнал без текста исключения.</summary>
    internal IReadOnlyList<string>? DiagnosticOtherVpnAdapters() => _options.Routes is { } routes
        ? OtherVpn.Find(routes.DefaultRoutes(), SingBoxConfigBuilder.InterfaceName)
        : null;
}
