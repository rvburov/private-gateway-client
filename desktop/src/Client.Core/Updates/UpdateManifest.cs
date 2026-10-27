using System.Globalization;
using System.Text.Json;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Core.Updates;

/// <summary>Файл выпуска для одной ОС: имя, SHA-256 и размер — из подписанного манифеста.</summary>
public sealed record UpdateAsset(string File, string Sha256, long Size);

/// <summary>
/// Манифест обновления <c>update.json</c> выпуска на GitHub (пишет scripts/release.cs): версия, канал, доля раскатки,
/// главное из заметок и файлы ОС с суммами. Читается только после проверки подписи (<see cref="UpdateSignature"/>) —
/// и все равно строго: лишнее или неверное — отказ, а не догадка.
/// </summary>
public sealed record UpdateManifest(
    string Version, UpdateChannel Channel, int Rollout, DateTimeOffset Published, Uri Notes, IReadOnlyList<string> Highlights,
    IReadOnlyDictionary<string, UpdateAsset> Assets)
{
    /// <summary>Больше манифест не бывает: файл длиннее — не он, читать дальше незачем.</summary>
    public const int MaxBytes = 64 * 1024;

    private const int MaxHighlights = 5;
    private const int MaxHighlightLength = 300;
    private const long MaxAssetSize = 2L * 1024 * 1024 * 1024;

    /// <exception cref="FormatException">Не манифест или в нем что-то не так.</exception>
    public static UpdateManifest Parse(ReadOnlySpan<byte> json)
    {
        if (json.Length > MaxBytes)
        {
            throw new FormatException("Манифест длиннее " + MaxBytes + " байт");
        }

        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            var root = document.RootElement;
            var version = Text(root, "version");
            if (!SemVer.IsValid(version))
            {
                throw new FormatException("Версия манифеста — не SemVer: " + version);
            }

            var channel = Text(root, "channel") switch
            {
                "stable" => UpdateChannel.Stable,
                "beta" => UpdateChannel.Beta,
                var other => throw new FormatException("Неизвестный канал: " + other),
            };
            var rollout = root.GetProperty("rollout").GetInt32();
            // 0 — раскатка остановлена (инцидент): выпуск больше никому не предлагается.
            if (rollout is < 0 or > 100)
            {
                throw new FormatException("Доля раскатки — от 0 до 100: " + rollout.ToString(CultureInfo.InvariantCulture));
            }

            var published = DateTimeOffset.Parse(Text(root, "published"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
            if (!Uri.TryCreate(Text(root, "notes"), UriKind.Absolute, out var notes) || notes.Scheme != Uri.UriSchemeHttps)
            {
                throw new FormatException("Заметки — не ссылка https");
            }

            List<string> highlights = root.TryGetProperty("highlights", out var list)
                ? [.. list.EnumerateArray().Take(MaxHighlights).Select(h => h.GetString() ?? string.Empty)
                    .Select(h => h.Length > MaxHighlightLength ? h[..MaxHighlightLength] : h)]
                : [];
            var assets = new Dictionary<string, UpdateAsset>(StringComparer.Ordinal);
            foreach (var asset in root.GetProperty("assets").EnumerateObject())
            {
                var file = Text(asset.Value, "file");
                var sha256 = Text(asset.Value, "sha256");
                var size = asset.Value.GetProperty("size").GetInt64();
                if (!IsKind(asset.Name) || !IsFileName(file) || sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigitLower) || size is < 1 or > MaxAssetSize)
                {
                    throw new FormatException("Неверный файл выпуска: " + asset.Name);
                }

                assets[asset.Name] = new UpdateAsset(file, sha256, size);
            }

            return new UpdateManifest(version, channel, rollout, published, notes, highlights, assets);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new FormatException("Манифест не читается: " + e.Message, e);
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.GetProperty(name).GetString() ?? throw new FormatException($"Нет поля {name}");

    // «win-x64-msi», «linux-x64-deb».
    private static bool IsKind(string kind) => kind.Length is > 0 and <= 40 && kind.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    // Только имя файла: никаких папок и «..» — файл ляжет в папку загрузок службы.
    private static bool IsFileName(string file) =>
        file.Length is > 0 and <= 200 && file[0] != '.' && file.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' or '~' or '+');
}
