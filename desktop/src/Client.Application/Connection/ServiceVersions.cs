using PrivateGateway.Client.Core;

namespace PrivateGateway.Client.Application.Connection;

/// <summary>Как служба относится к окну по версии.</summary>
public enum ServiceMatch
{
    /// <summary>Та же версия.</summary>
    Same,

    /// <summary>Служба старше окна: программу обновили, а службу (ее копию в своей папке) — нет.</summary>
    Older,

    /// <summary>Служба новее окна: обновили службу, а это окно — из прежней копии программы.</summary>
    Newer,

    /// <summary>Другой контракт IPC: окно и служба друг друга не поймут.</summary>
    Incompatible,
}

/// <summary>
/// Совместимость окна и службы. Ставятся они вместе, но служба работает из своей копии (папка службы) — после обновления
/// программы без переустановки службы версии расходятся.
/// </summary>
public static class ServiceVersions
{
    public static ServiceMatch Compare(string window, ServiceDetails service, string apiVersion)
    {
        ArgumentNullException.ThrowIfNull(service);
        if (!string.Equals(service.ApiVersion, apiVersion, StringComparison.Ordinal))
        {
            return ServiceMatch.Incompatible;
        }

        return SemVer.Compare(service.Version, window) switch
        {
            < 0 => ServiceMatch.Older,
            > 0 => ServiceMatch.Newer,
            _ => ServiceMatch.Same,
        };
    }
}
