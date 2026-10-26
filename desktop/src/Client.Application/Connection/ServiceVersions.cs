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

        return CompareSemVer(service.Version, window) switch
        {
            < 0 => ServiceMatch.Older,
            > 0 => ServiceMatch.Newer,
            _ => ServiceMatch.Same,
        };
    }

    /// <summary>
    /// Порядок версий SemVer 2.0: числа «1.2.3» по значению, выпуск старше своей предварительной версии («1.2.3-beta»),
    /// метаданные сборки («+abc») не в счет. Части предварительной версии: числа по значению и младше слов.
    /// </summary>
    public static int CompareSemVer(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var (leftCore, leftPre) = Split(left);
        var (rightCore, rightPre) = Split(right);
        for (var i = 0; i < 3; i++)
        {
            var order = Number(leftCore, i).CompareTo(Number(rightCore, i));
            if (order != 0)
            {
                return order;
            }
        }

        if (leftPre.Length == 0 || rightPre.Length == 0)
        {
            return rightPre.Length.CompareTo(leftPre.Length);
        }

        for (var i = 0; i < Math.Min(leftPre.Length, rightPre.Length); i++)
        {
            var leftNumeric = long.TryParse(leftPre[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = long.TryParse(rightPre[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var rightNumber);
            var order = (leftNumeric, rightNumeric) switch
            {
                (true, true) => leftNumber.CompareTo(rightNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(leftPre[i], rightPre[i]),
            };
            if (order != 0)
            {
                return Math.Sign(order);
            }
        }

        return leftPre.Length.CompareTo(rightPre.Length);

        static (string[] Core, string[] Pre) Split(string version)
        {
            var plain = version.Split('+')[0].Trim();
            var dash = plain.IndexOf('-', StringComparison.Ordinal);
            return dash < 0 ? (plain.Split('.'), []) : (plain[..dash].Split('.'), plain[(dash + 1)..].Split('.'));
        }

        static long Number(string[] parts, int index) =>
            index < parts.Length && long.TryParse(parts[index], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : 0;
    }
}
