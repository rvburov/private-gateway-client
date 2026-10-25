using System.Net;
using System.Text;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Infrastructure.Import;

/// <summary>
/// Загрузка подписки по http(s): не дольше <see cref="Timeout"/>, не больше <see cref="MaxBytes"/>
/// после распаковки. Адрес подписки — секрет: в ошибках только причина и код ответа. Название — из
/// заголовка Profile-Title.
/// </summary>
public sealed class HttpSubscriptionDownloader(HttpClient http, TimeProvider time) : ISubscriptionDownloader
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public const int MaxBytes = 5 * 1024 * 1024;

    /// <summary>Клиент для программы: сжатые ответы распаковываются, предел размера — после распаковки.</summary>
    public static HttpClient CreateClient() =>
        new(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All });

    public async Task<SubscriptionContent> DownloadAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.Scheme is not ("https" or "http"))
        {
            throw new ArgumentException("Подписка загружается только по http(s).", nameof(url));
        }

        using var timeout = new CancellationTokenSource(Timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("PrivateGateway/0.1");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new SubscriptionDownloadException(DownloadFailure.Status, (int)response.StatusCode);
            }

            if (response.Content.Headers.ContentLength > MaxBytes)
            {
                throw new SubscriptionDownloadException(DownloadFailure.TooLarge);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, linked.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxBytes)
                {
                    throw new SubscriptionDownloadException(DownloadFailure.TooLarge);
                }

                buffer.Write(chunk, 0, read);
            }

            var title = response.Headers.TryGetValues("Profile-Title", out var titles) ? titles.FirstOrDefault() : null;
            return new SubscriptionContent(
                Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), SubscriptionContent.TitleFrom(title));
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SubscriptionDownloadException(DownloadFailure.Timeout, innerException: e);
        }
        catch (HttpRequestException e)
        {
            // Текст исключения может содержать адрес — наружу только причина.
            throw new SubscriptionDownloadException(e.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => DownloadFailure.NameResolution,
                HttpRequestError.ConnectionError => DownloadFailure.Connection,
                HttpRequestError.SecureConnectionError => DownloadFailure.Tls,
                _ => DownloadFailure.Network,
            }, innerException: e);
        }
    }
}
