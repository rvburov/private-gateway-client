using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Application.Connection;

/// <summary>
/// Цель выбранного режима: VPN берет сохраненный сервер, прокси — отдельную внешнюю настройку.
/// Каталог VPN и секреты не меняются при переключении режима. Недавние несекретные описания позволяют
/// показать действующий выход до применения новой редакции формы.
/// </summary>
public sealed class ConnectionTarget
{
    private readonly IServerCatalog _catalog;
    private readonly SettingsService _settings;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ServerProfile> _external = new(StringComparer.Ordinal);
    private AppSettings _last;

    public ConnectionTarget(IServerCatalog catalog, SettingsService settings)
    {
        _catalog = catalog;
        _settings = settings;
        _last = settings.Current;
        Remember(_last.ExternalProxy.ToProfile());
        catalog.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        settings.Changed += (_, next) =>
        {
            if (!ReferenceEquals(next, settings.Current))
            {
                return;
            }

            var previous = _last;
            _last = next;
            if (previous.Mode != next.Mode || previous.ExternalProxy != next.ExternalProxy)
            {
                Remember(previous.ExternalProxy.ToProfile());
                Remember(next.ExternalProxy.ToProfile());
                Changed?.Invoke(this, EventArgs.Empty);
            }
        };
    }

    public bool IsExternalProxy => _settings.Current.Mode == ConnectionMode.Proxy;

    /// <summary>Неготовый внешний прокси не заменяется выбранным VPN-сервером.</summary>
    public ServerProfile? Selected => IsExternalProxy ? _settings.Current.ExternalProxy.ToProfile() : _catalog.Selected;

    public event EventHandler? Changed;

    /// <summary>Подключить VPN из карточки сервера — явный выбор более полного охвата трафика.</summary>
    public void UseVpnMode() => _settings.Update(s => s with { Mode = ConnectionMode.Vpn });

    /// <summary>Снимок службы имеет приоритет перед еще не примененной настройкой.</summary>
    public ServerProfile? InUse(ConnectionStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.State == ConnectionState.Off)
        {
            return Selected;
        }

        if (status.ServerId is null)
        {
            return status.Mode == ConnectionMode.Vpn ? _catalog.Selected : null;
        }

        if (ExternalProxySettings.IsExternalId(status.ServerId))
        {
            lock (_gate)
            {
                return _external.GetValueOrDefault(status.ServerId);
            }
        }

        return _catalog.Servers.FirstOrDefault(s => s.Id == status.ServerId)
            ?? (status.Mode == ConnectionMode.Vpn ? _catalog.Selected : null);
    }

    private void Remember(ServerProfile? profile)
    {
        if (profile is null)
        {
            return;
        }

        lock (_gate)
        {
            _external[profile.Id] = profile;
            while (_external.Count > 8)
            {
                _external.Remove(_external.Keys.First());
            }
        }
    }
}
