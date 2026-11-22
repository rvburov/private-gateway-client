namespace PrivateGateway.Client.Contracts;

/// <summary>Дополнительные представления IPC: старое окно не должно принимать местный фильтр за VPN.</summary>
public static class GatewayCapabilities
{
    public const string Header = "pg-client-capabilities";

    public const string LocalSiteFilter = "local-site-filter-v1";
}
