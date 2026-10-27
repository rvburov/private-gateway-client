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

    /// <summary>
    /// Версия та же, а исходники другие: сборка из исходников (у всех одна версия) обновлена через git, а установленная служба
    /// осталась из прежних — или служба коммита не называет (поставлена до того, как стала его называть). Какая из двух
    /// новее, по коммиту не узнать.
    /// </summary>
    OtherBuild,
}

/// <summary>
/// Совместимость окна и службы. Ставятся они вместе, но служба работает из своей копии (папка службы) — после обновления
/// программы без переустановки службы версии расходятся.
/// </summary>
public static class ServiceVersions
{
    /// <param name="window">Версия окна.</param>
    /// <param name="service">Что служба говорит о себе.</param>
    /// <param name="apiVersion">Контракт IPC окна.</param>
    /// <param name="windowBuild">
    /// Сборка окна — коммит исходников; пусто — сборки не сверяются. Служба без коммита при известном коммите окна — другая
    /// сборка: окно и служба одной копии программы собираются вместе, и у обеих коммит есть или нет.
    /// </param>
    public static ServiceMatch Compare(string window, ServiceDetails service, string apiVersion, string windowBuild = "")
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
            _ when windowBuild.Length > 0 && !string.Equals(service.Build, windowBuild, StringComparison.OrdinalIgnoreCase) => ServiceMatch.OtherBuild,
            _ => ServiceMatch.Same,
        };
    }
}
