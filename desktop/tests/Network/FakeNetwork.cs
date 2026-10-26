using PrivateGateway.Client.Service;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Смена сети по команде теста.</summary>
internal sealed class FakeNetwork : INetworkEvents
{
    public event EventHandler? Changed;

    public event EventHandler<TimeSpan>? Resumed;

    public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>Машина проснулась после сна длиной <paramref name="slept"/>.</summary>
    public void Wake(TimeSpan slept) => Resumed?.Invoke(this, slept);
}
