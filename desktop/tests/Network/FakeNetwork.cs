using PrivateGateway.Client.Service;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Смена сети по команде теста.</summary>
internal sealed class FakeNetwork : INetworkEvents
{
    public event EventHandler? Changed;

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
}
