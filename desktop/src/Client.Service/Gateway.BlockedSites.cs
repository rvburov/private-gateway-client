using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Service;

public sealed partial class Gateway
{
    private BlockedSiteHistory? _blockedSites;

    private BlockedSiteHistory BlockedSites => LazyInitializer.EnsureInitialized(ref _blockedSites, () => new BlockedSiteHistory(_time));

    public IReadOnlyList<BlockedSite> GetBlockedSites() => BlockedSites.Snapshot();

    private void CoreOutput(int session, string source, string line)
    {
        if (session != Volatile.Read(ref _session))
        {
            return;
        }
        if (source == "xray" && BlockedSites.Consume(line, () => session == Volatile.Read(ref _session)))
        {
            return;
        }
        if (source == "xray" && _request is { Connection.CoreLog: CoreLogLevel.Warning, Routing.HasSiteBlocks: true }
            && line.Contains("[Info]", StringComparison.Ordinal))
        {
            // Info включен внутри ядра только ради факта маршрутизации; выбранный пользователем Warning не меняется.
            return;
        }
        _journal.Add(CoreLines.Level(source, line), source, line);
    }
}
