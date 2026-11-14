using System.Text.Json;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Core.Updates;

/// <summary>
/// Выпуск в ленте обновлений сайта: версия и канал — как их назвал сайт (сверяются с подписанным манифестом), точные
/// байты манифеста <c>update.json</c> и его подпись (нет — null: выпуск пропускается с причиной), ссылки-билеты на файлы
/// ОС по виду файла манифеста (<c>win-x64-msi</c>, <c>linux-x64-deb</c>…).
/// </summary>
public sealed record ReleaseEntry(string Version, UpdateChannel Channel, byte[]? Manifest, string? Signature, IReadOnlyDictionary<string, Uri> Files)
{
    /// <summary>Больше лента не бывает: пять выпусков с манифестами — десятки килобайт; длиннее — не она, и не читается.</summary>
    public const int MaxFeedBytes = 1024 * 1024;

    /// <summary>Подпись — DER ECDSA P-256 в base64, около сотни знаков; длиннее — не она.</summary>
    public const int MaxSignature = 1024;

    /// <summary>
    /// Лента сайта (<c>GET /api/app/updates</c>):
    /// <c>{"releases":[{"version","channel","manifest","signature","files":{вид:{"url","expires_at"}}}]}</c>. Сайт — не
    /// источник доверия: выпуск с версией не по SemVer или незнакомым каналом пропускается, манифест — base64 точных байт
    /// (не base64 — манифеста нет), подпись — не длиннее <see cref="MaxSignature"/>. Ссылка на файл берется, только если
    /// это билет на адрес файлов <paramref name="files"/>: тот же адрес и после него один сегмент пути, без запроса и якоря;
    /// другие ссылки не берутся. Срок билета (<c>expires_at</c>) службе не нужен: истекший билет сайт не примет.
    /// </summary>
    /// <param name="json">Ответ сайта как есть.</param>
    /// <param name="files">Адрес файлов обновлений (<see cref="Account.AccountSite.UpdateFiles"/>, в тестах — свой).</param>
    /// <exception cref="FormatException">Ответ — не лента или длиннее <see cref="MaxFeedBytes"/>.</exception>
    public static IReadOnlyList<ReleaseEntry> ParseSite(ReadOnlySpan<byte> json, Uri files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (json.Length > MaxFeedBytes)
        {
            throw new FormatException("Лента обновлений сайта больше " + MaxFeedBytes / 1024 + " КБ");
        }

        var prefix = files.AbsoluteUri.EndsWith('/') ? files.AbsoluteUri : files.AbsoluteUri + "/";
        try
        {
            using var document = JsonDocument.Parse(json.ToArray());
            var releases = new List<ReleaseEntry>();
            foreach (var release in document.RootElement.GetProperty("releases").EnumerateArray())
            {
                var version = Text(release, "version");
                UpdateChannel? channel = Text(release, "channel") switch
                {
                    "stable" => UpdateChannel.Stable,
                    "beta" => UpdateChannel.Beta,
                    _ => null,
                };
                if (!SemVer.IsValid(version) || channel is not { } known)
                {
                    continue;
                }

                var signature = Text(release, "signature");
                releases.Add(new ReleaseEntry(version!, known, Base64(Text(release, "manifest")),
                    signature is { Length: <= MaxSignature } ? signature : null, Links(release, prefix)));
            }

            return releases;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FormatException("Ответ сайта — не лента обновлений: " + e.Message, e);
        }
    }

    // Ссылка-билет на файл: адрес файлов сайта и один непустой сегмент пути после него (билет), без запроса и якоря.
    // Сверяется уже разобранный адрес: «..», другой порт, имя пользователя или хост длиннее не пройдут.
    private static bool IsTicket(Uri url, string prefix)
    {
        var address = url.AbsoluteUri;
        return address.Length > prefix.Length && address.StartsWith(prefix, StringComparison.Ordinal)
            && address.AsSpan(prefix.Length).IndexOfAny('/', '?', '#') < 0;
    }

    private static Dictionary<string, Uri> Links(JsonElement release, string prefix)
    {
        var links = new Dictionary<string, Uri>(StringComparer.Ordinal);
        if (release.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Object)
        {
            foreach (var file in files.EnumerateObject())
            {
                if (Text(file.Value, "url") is { } text && Uri.TryCreate(text, UriKind.Absolute, out var url) && IsTicket(url, prefix))
                {
                    links[file.Name] = url;
                }
            }
        }

        return links;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static byte[]? Base64(string? text)
    {
        if (text is null)
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return null;
        }
    }
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

    /// <summary>Проверить не удалось: лента не получена, не читается или новый выпуск не прошел проверку доверия.</summary>
    Failed,

    /// <summary>
    /// Ленты сайта нет: ее берет окно своим входом в аккаунт, а входа нет (или окно старее службы и ленту не передает).
    /// Проверять нечего, в сеть никто не ходит; окно просит войти.
    /// </summary>
    NoFeed,
}

/// <summary>
/// Итог поиска: состояние, манифест найденного выпуска и что пропущено по дороге (для журнала); найденный выпуск в ленте —
/// <see cref="Release"/> (его ссылки на файлы).
/// </summary>
public sealed record UpdateSearch(UpdateState State, UpdateManifest? Manifest, IReadOnlyList<string> Skipped, ReleaseEntry? Release = null);

/// <summary>
/// Поиск обновления в ленте сайта. Выпуски — от новых к старым, своего канала (стабильный — без бет); первый не новее
/// установленной версии — конец поиска. Выпуск подходит, только если у него есть подпись, она сходится с ключом
/// программы над точными байтами манифеста, версия в манифесте — та же, что у выпуска в ленте, и номер этой установки
/// (0–99) меньше доли раскатки. Иначе — к следующему, более старому: пока новейший раскатывается, установка может
/// получить предыдущий. Версия, с которой пользователь откатился, не предлагается — только более новая. Сайт подделать
/// выпуск не может: закрытого ключа у него нет.
/// </summary>
public static class UpdateFinder
{
    /// <summary>Найти обновление; <paramref name="skip"/> — версия, с которой откатились (null — нет такой).</summary>
    public static UpdateSearch Find(
        IReadOnlyList<ReleaseEntry> releases, IReadOnlyList<string> publicKeys, string current, UpdateChannel channel, int bucket, string? skip = null,
        Func<UpdateManifest, byte[], string?>? acceptPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(publicKeys);
        ArgumentOutOfRangeException.ThrowIfNegative(bucket);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bucket, 99);
        if (publicKeys.Count == 0)
        {
            return new UpdateSearch(UpdateState.NotSigned, null, []);
        }

        ObservePolicies(releases, publicKeys, acceptPolicy);
        var skipped = new List<string>();
        var failed = false;
        foreach (var release in releases.Where(r => channel == UpdateChannel.Beta || r.Channel == UpdateChannel.Stable)
            .OrderByDescending(r => r.Version, Comparer<string>.Create(SemVer.Compare)))
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

            var (parsed, problem) = Verify(publicKeys, release, acceptPolicy);
            if (parsed is null)
            {
                failed = true;
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

            return new UpdateSearch(UpdateState.Available, parsed, skipped, release);
        }

        return new UpdateSearch(failed ? UpdateState.Failed : UpdateState.UpToDate, null, skipped);
    }

    /// <summary>
    /// Подписанный выпуск ровно этой версии (для отката на прежнюю или чтобы обещать откат на нынешнюю): подпись ключом
    /// программы, версия в манифесте. Канал и раскатка не важны — версию ставили на этой машине.
    /// </summary>
    /// <returns>Манифест или null и почему.</returns>
    public static (UpdateManifest? Manifest, string? Problem) Release(IReadOnlyList<ReleaseEntry> releases, IReadOnlyList<string> publicKeys, string version,
        Func<UpdateManifest, byte[], string?>? acceptPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentNullException.ThrowIfNull(publicKeys);
        if (publicKeys.Count == 0)
        {
            return (null, "в программе нет ключей подписи");
        }

        ObservePolicies(releases, publicKeys, acceptPolicy);
        var candidates = releases.Where(r => r.Version == version).Select(r => Verify(publicKeys, r, acceptPolicy, matchChannel: false)).ToList();
        return candidates.FirstOrDefault(r => r.Manifest is not null) is { Manifest: not null } found ? found
            : (null, candidates.FirstOrDefault().Problem ?? "выпуска нет в ленте сайта");
    }

    // Сначала запоминаются все подписанные изменения, в том числе стоп-кран и версии вне доли раскатки. Иначе
    // старый разрешающий манифест в начале той же ленты обошел бы остановку, стоящую ниже него.
    private static void ObservePolicies(IReadOnlyList<ReleaseEntry> releases, IReadOnlyList<string> publicKeys,
        Func<UpdateManifest, byte[], string?>? acceptPolicy)
    {
        if (acceptPolicy is null)
        {
            return;
        }

        foreach (var release in releases)
        {
            // Историю определяет подписанный канал, не внешняя метка сайта. Откат вправе взять уже установленную
            // версию из другого канала, поэтому и ее более новую политику надо увидеть до выбора дубликата.
            if (Verify(publicKeys, release, matchChannel: false).Manifest is { } manifest)
            {
                _ = acceptPolicy(manifest, release.Manifest!);
            }
        }
    }

    // Манифест выпуска и его подпись: подпись сходится с ключом программы над точными байтами, манифест разбирается,
    // версия в нем — та же, что у выпуска в ленте.
    private static (UpdateManifest? Manifest, string? Problem) Verify(IReadOnlyList<string> publicKeys, ReleaseEntry release,
        Func<UpdateManifest, byte[], string?>? acceptPolicy = null, bool matchChannel = true)
    {
        if (release.Manifest is null || release.Signature is null)
        {
            return (null, "выпуск без подписанного манифеста");
        }

        if (release.Manifest.Length > UpdateManifest.MaxBytes)
        {
            return (null, $"манифест больше {UpdateManifest.MaxBytes / 1024} КБ");
        }

        if (!UpdateSignature.Verify(release.Manifest, release.Signature, publicKeys))
        {
            return (null, "подпись манифеста не сходится с ключом программы");
        }

        UpdateManifest parsed;
        try
        {
            parsed = UpdateManifest.Parse(release.Manifest);
        }
        catch (FormatException e)
        {
            return (null, e.Message);
        }

        if (parsed.Version != release.Version || (matchChannel && parsed.Channel != release.Channel))
        {
            return (null, $"манифест от другого выпуска ({parsed.Version}, {parsed.Channel})");
        }

        return acceptPolicy?.Invoke(parsed, release.Manifest) is { } problem ? (null, problem) : (parsed, null);
    }
}
