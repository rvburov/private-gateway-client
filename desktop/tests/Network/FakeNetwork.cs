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

/// <summary>DNS ОС, который служба направляет на туннель: что и когда просили.</summary>
internal sealed class FakeSystemDns : PrivateGateway.Client.Core.Platform.ISystemDns
{
    private readonly List<string> _calls = [];

    public List<string> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public void Apply(System.Net.IPAddress server)
    {
        lock (_calls)
        {
            _calls.Add("apply " + server);
        }
    }

    public void Restore()
    {
        lock (_calls)
        {
            _calls.Add("restore");
        }
    }
}
