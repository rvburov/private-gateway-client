using System.Text;
using System.Text.Json;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Core.Updates;

/// <summary>Выпуск из списка на GitHub: версия из тега, предварительный ли, ссылки на манифест и его подпись (нет — null).</summary>
public sealed record ReleaseEntry(string Version, bool Prerelease, Uri? Manifest, Uri? Signature)
{
    /// <summary>
    /// Список выпусков из ответа GitHub (<c>GET /repos/{репозиторий}/releases</c>): черновики и теги не вида
    /// <c>v&lt;SemVer&gt;</c> пропускаются; ссылки на <c>update.json</c> и <c>update.json.sig</c> — только загрузки
    /// выпусков этого репозитория (<paramref name="downloads"/>, <c>https://github.com/{репозиторий}/releases/download/</c>)
    /// с тегом этого выпуска, другие не берутся.
    /// </summary>
    /// <exception cref="FormatException">Ответ — не список выпусков.</exception>
    public static IReadOnlyList<ReleaseEntry> ParseGitHub(ReadOnlySpan<byte> json, Uri downloads)
    {
        ArgumentNullException.ThrowIfNull(downloads);
        var prefix = downloads.AbsoluteUri.TrimEnd('/') + "/";
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            var releases = new List<ReleaseEntry>();
            foreach (var release in document.RootElement.EnumerateArray())
            {
                var tag = release.GetProperty("tag_name").GetString() ?? string.Empty;
                if (release.GetProperty("draft").GetBoolean() || !tag.StartsWith('v') || !SemVer.IsValid(tag[1..]))
                {
                    continue;
                }

                Uri? Asset(string name) => release.GetProperty("assets").EnumerateArray()
                    .Where(a => a.GetProperty("name").GetString() == name)
                    .Select(a => a.GetProperty("browser_download_url").GetString())
                    .Where(url => url is not null && url.StartsWith(prefix + tag + "/", StringComparison.Ordinal))
                    .Select(url => new Uri(url!))
                    .FirstOrDefault();
                releases.Add(new ReleaseEntry(tag[1..], release.GetProperty("prerelease").GetBoolean(), Asset("update.json"), Asset("update.json.sig")));
            }

            return releases;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or UriFormatException)
        {
            throw new FormatException("Ответ GitHub — не список выпусков: " + e.Message, e);
        }
    }
}

/// <summary>Откуда берутся выпуски: список и загрузка файла (служба — GitHub по HTTPS, тесты — свое).</summary>
public interface IReleaseFeed
{
    Task<IReadOnlyList<ReleaseEntry>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Файл целиком, не длиннее <paramref name="maxBytes"/> (длиннее — <see cref="InvalidDataException"/>).</summary>
    Task<byte[]> DownloadAsync(Uri url, int maxBytes, CancellationToken cancellationToken);
}

/// <summary>Что с обновлением.</summary>
public enum UpdateState
{
    /// <summary>В программе нет ключей подписи: обновления начнутся с первого подписанного выпуска.</summary>
    NotSigned,

    /// <summary>Новее нет (или новый выпуск до этой установки еще не дошел — раскатка).</summary>
    UpToDate,

    /// <summary>Есть новее, подпись сходится.</summary>
    Available,

    /// <summary>Проверить не удалось: нет сети, GitHub не ответил.</summary>
    Failed,
}

/// <summary>Итог поиска: состояние, манифест найденного выпуска и что пропущено по дороге (для журнала).</summary>
public sealed record UpdateSearch(UpdateState State, UpdateManifest? Manifest, IReadOnlyList<string> Skipped);

/// <summary>
/// Поиск обновления. Выпуски — от новых к старым, своего канала (стабильный — без предварительных); первый не новее
/// установленной версии — конец поиска. Выпуск подходит, только если у него есть подпись, она сходится с ключом
/// программы, версия в манифесте — та же, что в теге, и номер этой установки (0–99) меньше доли раскатки. Иначе — к
/// следующему, более старому: пока новейший раскатывается, установка может получить предыдущий.
/// </summary>
public static class UpdateFinder
{
    /// <exception cref="HttpRequestException">И другие ошибки <paramref name="feed"/> — проверка не удалась.</exception>
    public static async Task<UpdateSearch> FindAsync(
        IReleaseFeed feed, IReadOnlyList<string> publicKeys, string current, UpdateChannel channel, int bucket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(publicKeys);
        ArgumentOutOfRangeException.ThrowIfNegative(bucket);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bucket, 99);
        if (publicKeys.Count == 0)
        {
            return new UpdateSearch(UpdateState.NotSigned, null, []);
        }

        var skipped = new List<string>();
        var releases = (await feed.ListAsync(cancellationToken).ConfigureAwait(false))
            .Where(r => channel == UpdateChannel.Beta || !r.Prerelease)
            .OrderByDescending(r => r.Version, Comparer<string>.Create(SemVer.Compare));
        foreach (var release in releases)
        {
            if (SemVer.Compare(release.Version, current) <= 0)
            {
                break;
            }

            if (release.Manifest is null || release.Signature is null)
            {
                skipped.Add($"{release.Version}: выпуск без подписанного манифеста");
                continue;
            }

            var manifest = await feed.DownloadAsync(release.Manifest, UpdateManifest.MaxBytes, cancellationToken).ConfigureAwait(false);
            var signature = await feed.DownloadAsync(release.Signature, 1024, cancellationToken).ConfigureAwait(false);
            if (!UpdateSignature.Verify(manifest, Encoding.ASCII.GetString(signature), publicKeys))
            {
                skipped.Add($"{release.Version}: подпись манифеста не сходится с ключом программы");
                continue;
            }

            UpdateManifest parsed;
            try
            {
                parsed = UpdateManifest.Parse(manifest);
            }
            catch (FormatException e)
            {
                skipped.Add($"{release.Version}: {e.Message}");
                continue;
            }

            if (parsed.Version != release.Version || (parsed.Channel == UpdateChannel.Beta && channel == UpdateChannel.Stable))
            {
                skipped.Add($"{release.Version}: манифест от другого выпуска ({parsed.Version}, {parsed.Channel})");
                continue;
            }

            if (bucket >= parsed.Rollout)
            {
                skipped.Add($"{release.Version}: раскатка {parsed.Rollout} %, до этой установки еще не дошла");
                continue;
            }

            return new UpdateSearch(UpdateState.Available, parsed, skipped);
        }

        return new UpdateSearch(UpdateState.UpToDate, null, skipped);
    }
}
