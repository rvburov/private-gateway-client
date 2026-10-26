using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Core.Updates;

namespace PrivateGateway.Client.Service.Updates;

/// <summary>
/// Проверка обновлений для окна (CheckForUpdate): поиск выпуска (<see cref="UpdateFinder"/>) по ключам программы и номеру
/// этой установки, ответ помнится час на канал — окно спрашивает при открытии «О программе» и по расписанию, а GitHub
/// лишний раз не дергается. Неудача не запоминается: следующий вопрос — новая попытка. Что пропущено и почему — в журнал.
/// Тем же списком выпусков — откат: прежняя версия (<see cref="VersionHistory"/>) с подписанным выпуском и файлом этой
/// установки, и можно ли будет вернуться на нынешнюю после обновления; версия, с которой откатились, не предлагается.
/// </summary>
/// <param name="feed">Выпуски: GitHub, в тестах — свое.</param>
/// <param name="keys">Открытые ключи подписи обновлений.</param>
/// <param name="current">Версия этой службы.</param>
/// <param name="bucket">Номер установки для раскатки, 0–99.</param>
/// <param name="time">Часы: ответ помнится час.</param>
/// <param name="journal">Журнал службы: что пропущено и почему.</param>
/// <param name="history">Прежняя версия и та, с которой откатились; null — истории нет.</param>
/// <param name="asset">Файл выпуска, которым ставит эта установка (<see cref="UpdateKind.Asset"/>); null — служба не
/// ставит, и откатывать ей нечем.</param>
public sealed class UpdateChecker(
    IReleaseFeed feed, IReadOnlyList<string> keys, string current, int bucket, TimeProvider time, ServiceJournal journal, VersionHistory? history = null,
    string? asset = null) : IDisposable
{
    private static readonly TimeSpan Remember = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (UpdateChannel Channel, UpdateOffer Offer, UpdateManifest? Manifest, UpdateManifest? Rollback)? _last;
    private string? _rollbackProblem;

    /// <summary>Номер установки для раскатки, 0–99.</summary>
    public int Bucket => bucket;

    /// <summary>Манифест последнего найденного выпуска (для установки обновления).</summary>
    public UpdateManifest? Found => _last?.Manifest;

    /// <summary>Манифест прежней версии, на которую можно откатиться (последняя проверка); null — не на что.</summary>
    public UpdateManifest? Rollback => _last?.Rollback;

    /// <summary>Без ключей подписи: ответ всегда «нет подписанных выпусков», в сеть не ходит.</summary>
    public static UpdateChecker WithoutKeys(ServiceJournal journal) => new(new NoFeed(), [], ServiceVersion.Current, 0, TimeProvider.System, journal);

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

    public async Task<UpdateOffer> CheckAsync(UpdateChannel channel, bool force, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = time.GetUtcNow();
            if (!force && _last is { } last && last.Channel == channel && now - last.Offer.Checked < Remember)
            {
                return last.Offer;
            }

            try
            {
                // Список выпусков — один раз на проверку: и для поиска, и для отката.
                var releases = new ListOnce(feed);
                var search = await UpdateFinder.FindAsync(releases, keys, current, channel, bucket, history?.Left, cancellationToken).ConfigureAwait(false);
                foreach (var skipped in search.Skipped)
                {
                    journal.Add(JournalLevel.Warning, "update", "Выпуск пропущен: " + skipped);
                }

                var rollback = await RollbackAsync(releases, cancellationToken).ConfigureAwait(false);
                var returnTo = asset is not null && search.Manifest is not null
                    && (await UpdateFinder.ReleaseAsync(releases, keys, current, cancellationToken).ConfigureAwait(false)).Manifest?.Assets.ContainsKey(asset) == true
                    ? current : null;
                var found = search.Manifest is { } manifest ? UpdateOffer.Found(manifest, now) : new UpdateOffer(search.State, now);
                var offer = found with { Rollback = rollback?.Version, ReturnTo = returnTo };
                if (offer.State == UpdateState.Available && _last?.Offer.Version != offer.Version)
                {
                    journal.Add(JournalLevel.Info, "update", $"Доступно обновление {offer.Version} (подпись проверена)");
                }

                _last = (channel, offer, search.Manifest, rollback);
                return offer;
            }
            catch (Exception e) when (e is HttpRequestException or InvalidDataException or FormatException or IOException
                || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                journal.Add(JournalLevel.Warning, "update", "Проверить обновления не удалось: " + e.Message);
                return new UpdateOffer(UpdateState.Failed, now);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    // Прежняя версия старше нынешней, ее выпуск подписан и в нем есть файл этой установки; почему нет — в журнал, когда меняется.
    private async Task<UpdateManifest?> RollbackAsync(IReleaseFeed releases, CancellationToken cancellationToken)
    {
        if (asset is null || history?.Previous is not { } previous || SemVer.Compare(previous, current) >= 0)
        {
            return null;
        }

        var (manifest, problem) = await UpdateFinder.ReleaseAsync(releases, keys, previous, cancellationToken).ConfigureAwait(false);
        problem ??= manifest!.Assets.ContainsKey(asset) ? null : "в выпуске нет файла для этой установки";
        if (problem is not null && problem != _rollbackProblem)
        {
            journal.Add(JournalLevel.Info, "update", $"Откатиться на {previous} нельзя: {problem}");
        }

        _rollbackProblem = problem;
        return problem is null ? manifest : null;
    }

    /// <summary>Источник выпусков, который отдает список один раз.</summary>
    private sealed class ListOnce(IReleaseFeed feed) : IReleaseFeed
    {
        private Task<IReadOnlyList<ReleaseEntry>>? _list;

        public Task<IReadOnlyList<ReleaseEntry>> ListAsync(CancellationToken cancellationToken) => _list ??= feed.ListAsync(cancellationToken);

        public Task<byte[]> DownloadAsync(Uri url, int maxBytes, CancellationToken cancellationToken) => feed.DownloadAsync(url, maxBytes, cancellationToken);
    }

    // Без ключей поиск до выпусков не доходит.
    private sealed class NoFeed : IReleaseFeed
    {
        public Task<IReadOnlyList<ReleaseEntry>> ListAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Без ключей подписи выпуски не ищутся");

        public Task<byte[]> DownloadAsync(Uri url, int maxBytes, CancellationToken cancellationToken) => throw new InvalidOperationException("Без ключей подписи выпуски не ищутся");
    }
}
