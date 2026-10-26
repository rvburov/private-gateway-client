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
/// </summary>
public sealed class UpdateChecker(IReleaseFeed feed, IReadOnlyList<string> keys, string current, int bucket, TimeProvider time, ServiceJournal journal)
    : IDisposable
{
    private static readonly TimeSpan Remember = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (UpdateChannel Channel, UpdateOffer Offer, UpdateManifest? Manifest)? _last;

    /// <summary>Номер установки для раскатки, 0–99.</summary>
    public int Bucket => bucket;

    /// <summary>Манифест последнего найденного выпуска (для установки обновления).</summary>
    public UpdateManifest? Found => _last?.Manifest;

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
                var search = await UpdateFinder.FindAsync(feed, keys, current, channel, bucket, cancellationToken).ConfigureAwait(false);
                foreach (var skipped in search.Skipped)
                {
                    journal.Add(JournalLevel.Warning, "update", "Выпуск пропущен: " + skipped);
                }

                var offer = search.Manifest is { } manifest ? UpdateOffer.Found(manifest, now) : new UpdateOffer(search.State, now);
                if (offer.State == UpdateState.Available && _last?.Offer.Version != offer.Version)
                {
                    journal.Add(JournalLevel.Info, "update", $"Доступно обновление {offer.Version} (подпись проверена)");
                }

                _last = (channel, offer, search.Manifest);
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

    // Без ключей поиск до выпусков не доходит.
    private sealed class NoFeed : IReleaseFeed
    {
        public Task<IReadOnlyList<ReleaseEntry>> ListAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Без ключей подписи выпуски не ищутся");

        public Task<byte[]> DownloadAsync(Uri url, int maxBytes, CancellationToken cancellationToken) => throw new InvalidOperationException("Без ключей подписи выпуски не ищутся");
    }
}
