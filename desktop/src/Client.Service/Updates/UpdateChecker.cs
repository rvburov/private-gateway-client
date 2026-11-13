using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Account;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Core.Updates;

namespace PrivateGateway.Client.Service.Updates;

/// <summary>
/// Проверка обновлений для окна (CheckForUpdate) по ленте сайта: ее берет окно своим входом в аккаунт и передает как есть
/// (у службы токена нет). Сайт — не источник доверия: подпись каждого манифеста служба проверяет ключами, встроенными в
/// себя (<see cref="UpdateFinder"/>), долю раскатки — номером этой установки; в сеть при проверке не ходит. Без ключей —
/// «нет подписанных выпусков», без ленты — <see cref="UpdateState.NoFeed"/>; что пропущено и почему — в журнал. Той же
/// лентой — откат: прежняя версия (<see cref="VersionHistory"/>) с подписанным выпуском и файлом этой установки, и можно
/// ли будет вернуться на нынешнюю после обновления; версия, с которой откатились, не предлагается. Ссылку на файл для
/// загрузки (<see cref="Link"/>) служба берет из свежей ленты: билеты сайта живут 30 минут.
/// </summary>
/// <param name="keys">Открытые ключи подписи обновлений.</param>
/// <param name="current">Версия этой службы.</param>
/// <param name="bucket">Номер установки для раскатки, 0–99.</param>
/// <param name="time">Часы: когда проверяли.</param>
/// <param name="journal">Журнал службы: что пропущено и почему.</param>
/// <param name="history">Прежняя версия и та, с которой откатились; null — истории нет.</param>
/// <param name="asset">Файл выпуска, которым ставит эта установка (<see cref="UpdateKind.Asset"/>); null — служба не
/// ставит, и откатывать ей нечем. Ставит служба выпуск, только если сайт дал на этот файл ссылку.</param>
/// <param name="files">Адрес файлов обновлений: ссылки на файлы — только билеты на него; null — сайт
/// (<see cref="AccountSite.UpdateFiles"/>). Только https; http — только к своему компьютеру (тесты).</param>
public sealed class UpdateChecker(
    IReadOnlyList<string> keys, string current, int bucket, TimeProvider time, ServiceJournal journal, VersionHistory? history = null,
    string? asset = null, Uri? files = null)
{
    private readonly Lock _gate = new();
    private readonly Uri _files = Checked(files ?? AccountSite.UpdateFiles);
    private Last? _last;
    private string? _rollbackProblem;

    /// <summary>Номер установки для раскатки, 0–99.</summary>
    public int Bucket => bucket;

    /// <summary>Манифест последнего найденного выпуска (для установки обновления).</summary>
    public UpdateManifest? Found => _last?.Manifest;

    /// <summary>Манифест прежней версии, на которую можно откатиться (последняя проверка); null — не на что.</summary>
    public UpdateManifest? Rollback => _last?.Rollback;

    /// <summary>Без ключей подписи: ответ всегда «нет подписанных выпусков», ленту не читает.</summary>
    public static UpdateChecker WithoutKeys(ServiceJournal journal) => new([], ServiceVersion.Current, 0, TimeProvider.System, journal);

    /// <summary>Открытые ключи подписи обновлений, встроенные в службу (shared/update/keys/*.pem).</summary>
    public static IReadOnlyList<string> EmbeddedKeys()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return [.. assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("update-keys/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(n =>
            {
                using var reader = new StreamReader(assembly.GetManifestResourceStream(n)!);
                return reader.ReadToEnd();
            })];
    }

    /// <summary>
    /// Номер этой установки для поэтапной раскатки — случайный 0–99, один раз и навсегда (файл <c>update-bucket</c> в
    /// папке состояния). Не идентификатор: по нему установку не отличить от сотой части других.
    /// </summary>
    public static int LoadBucket(string folder)
    {
        var path = Path.Combine(folder, "update-bucket");
        try
        {
            if (int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var saved) && saved is >= 0 and <= 99)
            {
                return saved;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Нет файла — новый номер.
        }

        var bucket = RandomNumberGenerator.GetInt32(100);
        try
        {
            File.WriteAllText(path, bucket.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Не записался — номер на этот запуск; раскатка от этого только чуть менее ровная.
        }

        return bucket;
    }

    /// <summary>
    /// Проверить ленту <paramref name="feed"/> (ответ сайта как есть, пусто — окно ее не передало). Ответ не помнится:
    /// лента каждый раз новая, а проверка — без сети. Неудача найденного прежде не стирает.
    /// </summary>
    public UpdateOffer Check(UpdateChannel channel, ReadOnlySpan<byte> feed)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (keys.Count == 0)
            {
                return new UpdateOffer(UpdateState.NotSigned, now);
            }

            if (feed.IsEmpty)
            {
                journal.Add(JournalLevel.Info, "update", "Окно не передало ленту обновлений сайта (вход в аккаунт не выполнен или окно старее службы) — проверять нечего");
                return new UpdateOffer(UpdateState.NoFeed, now);
            }

            IReadOnlyList<ReleaseEntry> releases;
            try
            {
                releases = ReleaseEntry.ParseSite(feed, _files);
            }
            catch (FormatException e)
            {
                journal.Add(JournalLevel.Warning, "update", "Проверить обновления не удалось: " + e.Message);
                return new UpdateOffer(UpdateState.Failed, now);
            }

            var search = UpdateFinder.Find(releases, keys, current, channel, bucket, history?.Left);
            foreach (var skipped in search.Skipped)
            {
                journal.Add(JournalLevel.Warning, "update", "Выпуск пропущен: " + skipped);
            }

            var rollback = RollbackOf(releases);
            var returnTo = asset is not null && search.Manifest is not null && Installable(releases, current).Manifest is not null ? current : null;

            // Ставит служба только то, на что сайт дал ссылку для этой установки: выпуск без файлов ОС (пустые files) —
            // не ошибка, а «скачать на сайте».
            var installable = asset is not null && search.Release?.Files.ContainsKey(asset) == true;
            var found = search.Manifest is { } manifest ? UpdateOffer.Found(manifest, now) with { Installable = installable } : new UpdateOffer(search.State, now);
            var offer = found with { Rollback = rollback?.Version, ReturnTo = returnTo };
            if (offer.State == UpdateState.Available && _last?.Offer.Version != offer.Version)
            {
                journal.Add(JournalLevel.Info, "update", $"Доступно обновление {offer.Version} (подпись проверена)"
                    + (asset is not null && !installable ? $"; файла {asset} на сайте нет — служба его не поставит" : string.Empty));
            }

            _last = new Last(offer, search.Manifest, rollback);
            return offer;
        }
    }

    /// <summary>
    /// Ссылка-билет на файл <paramref name="kind"/> (<see cref="UpdateKind.Asset"/>) версии <paramref name="version"/> из
    /// свежей ленты <paramref name="feed"/> (перед загрузкой окно берет ленту заново: билеты живут 30 минут). Только билет
    /// на адрес файлов сайта; нет его — null, почему — в журнал. Сам файл сверяется с манифестом, найденным проверкой, а
    /// не с этой лентой.
    /// </summary>
    public Uri? Link(string version, string kind, ReadOnlySpan<byte> feed)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(kind);
        string problem;
        if (feed.IsEmpty)
        {
            problem = "окно не передало ленту сайта";
        }
        else
        {
            try
            {
                if (ReleaseEntry.ParseSite(feed, _files).FirstOrDefault(r => r.Version == version)?.Files.GetValueOrDefault(kind) is { } url)
                {
                    return url;
                }

                problem = $"в ленте сайта нет ссылки на файл {kind}";
            }
            catch (FormatException e)
            {
                problem = e.Message;
            }
        }

        journal.Add(JournalLevel.Warning, "update", $"Обновление {version} не скачать: {problem}");
        return null;
    }

    // Адрес файлов — только https; http — только к своему компьютеру (тесты).
    private static Uri Checked(Uri files)
    {
        if (!files.IsAbsoluteUri || !(files.Scheme == Uri.UriSchemeHttps || (files.Scheme == Uri.UriSchemeHttp && files.IsLoopback)))
        {
            throw new ArgumentException("Файлы обновлений — только по https (http — только к своему компьютеру).", nameof(files));
        }

        return files;
    }

    // Прежняя версия старше нынешней, ее выпуск подписан и служба может его поставить; почему нет — в журнал, когда меняется.
    private UpdateManifest? RollbackOf(IReadOnlyList<ReleaseEntry> releases)
    {
        if (asset is null || history?.Previous is not { } previous || SemVer.Compare(previous, current) >= 0)
        {
            return null;
        }

        var (manifest, problem) = Installable(releases, previous);
        if (problem is not null && problem != _rollbackProblem)
        {
            journal.Add(JournalLevel.Info, "update", $"Откатиться на {previous} нельзя: {problem}");
        }

        _rollbackProblem = problem;
        return manifest;
    }

    // Подписанный выпуск ровно этой версии, который служба может поставить: файл этой установки есть и в манифесте, и на
    // сайте (ссылка в ленте; выпуск без файлов ОС сайт отдает с пустым files). Нет — null и почему.
    private (UpdateManifest? Manifest, string? Problem) Installable(IReadOnlyList<ReleaseEntry> releases, string version)
    {
        var (manifest, problem) = UpdateFinder.Release(releases, keys, version);
        problem ??= !manifest!.Assets.ContainsKey(asset!) ? "в выпуске нет файла для этой установки"
            : releases.First(r => r.Version == version).Files.ContainsKey(asset!) ? null
            : "файла для этой установки нет на сайте";
        return problem is null ? (manifest, null) : (null, problem);
    }

    /// <summary>Последняя удачная проверка: ответ окну, найденный выпуск и прежняя версия для отката.</summary>
    private sealed record Last(UpdateOffer Offer, UpdateManifest? Manifest, UpdateManifest? Rollback);
}
