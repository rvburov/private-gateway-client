using System.Text;

namespace PrivateGateway.Client.Application.Import;

/// <summary>
/// Флаг страны в начале названия («🇳🇱 Нидерланды 01») — код страны для значка и название без
/// флага: шрифт интерфейса цветных флагов не рисует, а значок «NL» есть в макете.
/// </summary>
public static class CountryFlag
{
    private const int RegionalA = 0x1F1E6;
    private const int RegionalZ = 0x1F1FF;

    /// <returns>Код страны (пусто — флага нет) и название без флага.</returns>
    public static (string Country, string Name) Split(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var runes = name.Trim().EnumerateRunes().ToList();
        if (runes.Count >= 2 && IsRegional(runes[0]) && IsRegional(runes[1]))
        {
            var code = string.Concat((char)('A' + runes[0].Value - RegionalA), (char)('A' + runes[1].Value - RegionalA));
            var rest = new StringBuilder();
            foreach (var rune in runes.Skip(2))
            {
                rest.Append(rune.ToString());
            }

            return (code, rest.ToString().Trim());
        }

        return (string.Empty, name.Trim());
    }

    private static bool IsRegional(Rune rune) => rune.Value is >= RegionalA and <= RegionalZ;
}
