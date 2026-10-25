using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Маршруты ко всему интернету, которые задает тест.</summary>
internal sealed class FakeRoutes : IRouteTable
{
    public IReadOnlyList<DefaultRoute> Routes { get; set; } = [];

    public IReadOnlyList<DefaultRoute> DefaultRoutes() => Routes;
}
