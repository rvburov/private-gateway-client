using System.Net;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Network.Xray;

namespace PrivateGateway.Client.Service.Cores;

/// <summary>Ограниченная загрузка через фиксированный парольный SOCKS ядра, без обхода и прямого повтора.</summary>
public static class SubscriptionTransport
{
    public static async Task<SubscriptionDownload> DownloadAsync(ProbeLink link, Uri address, TimeProvider time, CancellationToken cancellationToken = default)
    {
        using var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy(new Uri($"socks5://127.0.0.1:{link.Port}")) { Credentials = new NetworkCredential(link.User, link.Password) },
            UseProxy = true,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = new CancellationTokenSource(SubscriptionDownload.Timeout, time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.UserAgent.ParseAdd("PrivateGateway/0.1");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stop.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return SubscriptionDownload.Failed(DownloadFailure.Status, (int)response.StatusCode);
            }

            if (response.Content.Headers.ContentLength > SubscriptionDownload.MaxBytes)
            {
                return SubscriptionDownload.Failed(DownloadFailure.TooLarge);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(stop.Token).ConfigureAwait(false);
            using var content = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, stop.Token).ConfigureAwait(false)) > 0)
            {
                if (content.Length + read > SubscriptionDownload.MaxBytes)
                {
                    return SubscriptionDownload.Failed(DownloadFailure.TooLarge);
                }

                content.Write(buffer, 0, read);
            }

            return new SubscriptionDownload(content.ToArray(), response.Headers.TryGetValues("Profile-Title", out var titles) ? titles.FirstOrDefault() : null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SubscriptionDownload.Failed(DownloadFailure.Timeout);
        }
        catch (HttpRequestException e)
        {
            return SubscriptionDownload.Failed(e.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => DownloadFailure.NameResolution,
                HttpRequestError.SecureConnectionError => DownloadFailure.Tls,
                HttpRequestError.ConnectionError or HttpRequestError.ProxyTunnelError => DownloadFailure.Connection,
                _ => DownloadFailure.Network,
            });
        }
        catch (IOException)
        {
            return SubscriptionDownload.Failed(DownloadFailure.Network);
        }
    }
}
