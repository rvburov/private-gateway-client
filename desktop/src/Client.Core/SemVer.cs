using System.Globalization;
using System.Text.RegularExpressions;

namespace PrivateGateway.Client.Core;

/// <summary>Версии SemVer 2.0: окна, службы и выпусков (обновления).</summary>
public static partial class SemVer
{
    /// <summary>Версия ли это: «1.2.3», «1.2.3-beta.1», «1.2.3+abc» — без «v» впереди, пробелов и лишнего.</summary>
    public static bool IsValid(string? version) => version is not null && Pattern().IsMatch(version);

    /// <summary>
    /// Метаданные сборки — то, что после «+»: у сборки из git .NET кладет туда коммит исходников («0.1.0+e7ddb17…»); нет — пусто.
    /// По ним окно узнает службу из других исходников той же версии (<see cref="Compare"/> их не учитывает).
    /// </summary>
    public static string Build(string? version)
    {
        var plus = version?.IndexOf('+', StringComparison.Ordinal) ?? -1;
        return plus < 0 ? string.Empty : version![(plus + 1)..].Trim();
    }

    /// <summary>
    /// Порядок версий SemVer 2.0: числа «1.2.3» по значению, выпуск старше своей предварительной версии («1.2.3-beta»),
    /// метаданные сборки («+abc») не в счет. Части предварительной версии: числа по значению и младше слов.
    /// </summary>
    public static int Compare(string left, string right)
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
            var leftNumeric = long.TryParse(leftPre[i], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = long.TryParse(rightPre[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
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
            index < parts.Length && long.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex Pattern();
}
