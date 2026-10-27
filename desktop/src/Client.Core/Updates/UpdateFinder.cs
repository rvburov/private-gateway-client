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
/// следующему, более старому: пока новейший раскатывается, установка может получить предыдущий. Версия, с которой
/// пользователь откатился, не предлагается — только более новая.
/// </summary>
public static class UpdateFinder
{
    /// <summary>Найти обновление; <paramref name="skip"/> — версия, с которой откатились (null — нет такой).</summary>
    /// <exception cref="HttpRequestException">И другие ошибки <paramref name="feed"/> — проверка не удалась.</exception>
    public static async Task<UpdateSearch> FindAsync(
        IReleaseFeed feed, IReadOnlyList<string> publicKeys, string current, UpdateChannel channel, int bucket, string? skip = null,
        CancellationToken cancellationToken = default)
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

            if (release.Version == skip)
            {
                skipped.Add($"{release.Version}: с этой версии откатились — предлагается только более новая");
                continue;
            }

            var (parsed, problem) = await VerifyAsync(feed, publicKeys, release, cancellationToken).ConfigureAwait(false);
            if (parsed is null)
            {
                skipped.Add($"{release.Version}: {problem}");
                continue;
            }

            if (parsed.Channel == UpdateChannel.Beta && channel == UpdateChannel.Stable)
            {
                skipped.Add($"{release.Version}: манифест от другого выпуска ({parsed.Version}, {parsed.Channel})");
                continue;
            }

            if (parsed.Rollout == 0)
            {
                skipped.Add($"{release.Version}: раскатка остановлена");
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

    /// <summary>
    /// Подписанный выпуск ровно этой версии (для отката на прежнюю или чтобы обещать откат на нынешнюю): тег, подпись
    /// ключом программы, версия в манифесте. Канал и раскатка не важны — версию ставили на этой машине.
    /// </summary>
    /// <returns>Манифест или null и почему.</returns>
    /// <exception cref="HttpRequestException">И другие ошибки <paramref name="feed"/>.</exception>
    public static async Task<(UpdateManifest? Manifest, string? Problem)> ReleaseAsync(
        IReleaseFeed feed, IReadOnlyList<string> publicKeys, string version, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(publicKeys);
        if (publicKeys.Count == 0)
        {
            return (null, "в программе нет ключей подписи");
        }

        var release = (await feed.ListAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(r => r.Version == version);
        return release is null ? (null, "выпуска нет на GitHub") : await VerifyAsync(feed, publicKeys, release, cancellationToken).ConfigureAwait(false);
    }

    // Манифест выпуска и его подпись: подпись сходится с ключом программы, манифест разбирается, версия в нем — из тега.
    private static async Task<(UpdateManifest? Manifest, string? Problem)> VerifyAsync(
        IReleaseFeed feed, IReadOnlyList<string> publicKeys, ReleaseEntry release, CancellationToken cancellationToken)
    {
        if (release.Manifest is null || release.Signature is null)
        {
            return (null, "выпуск без подписанного манифеста");
        }

        var manifest = await feed.DownloadAsync(release.Manifest, UpdateManifest.MaxBytes, cancellationToken).ConfigureAwait(false);
        var signature = await feed.DownloadAsync(release.Signature, 1024, cancellationToken).ConfigureAwait(false);
        if (!UpdateSignature.Verify(manifest, Encoding.ASCII.GetString(signature), publicKeys))
        {
            return (null, "подпись манифеста не сходится с ключом программы");
        }

        UpdateManifest parsed;
        try
        {
            parsed = UpdateManifest.Parse(manifest);
        }
        catch (FormatException e)
        {
            return (null, e.Message);
        }

        return parsed.Version != release.Version
            ? (null, $"манифест от другого выпуска ({parsed.Version}, {parsed.Channel})")
            : (parsed, null);
    }
}
