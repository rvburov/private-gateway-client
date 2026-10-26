using PrivateGateway.Client.Core.Updates;

namespace PrivateGateway.Client.Service.Updates;

/// <summary>
/// Выпуски программы на GitHub: список — API (<see cref="DefaultReleases"/>), файлы — загрузки выпусков репозитория
/// (<see cref="DefaultDownloads"/>; GitHub переадресует на свое хранилище). Без учетной записи: репозиторий открытый,
/// а лимит API (60 запросов в час с адреса) проверке раз в несколько часов не мешает.
/// </summary>
public sealed class GitHubReleaseFeed(HttpClient http, Uri releases, Uri downloads) : IReleaseFeed
{
    public const string Repository = "rvburov/private-gateway-client";

    /// <summary>Двадцать последних выпусков: обновление ищется среди них.</summary>
    public static Uri DefaultReleases { get; } = new($"https://api.github.com/repos/{Repository}/releases?per_page=20");

    public static Uri DefaultDownloads { get; } = new($"https://github.com/{Repository}/releases/download/");

    private const int MaxList = 4 * 1024 * 1024;

    /// <summary>HTTP для проверки обновлений: только HTTPS-переадресации, ограничение по времени, имя программы (GitHub без него не отвечает).</summary>
    public static HttpClient CreateHttp()
    {
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 5, ConnectTimeout = TimeSpan.FromSeconds(15) })
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PrivateGateway-Updates");
        return http;
    }

    public async Task<IReadOnlyList<ReleaseEntry>> ListAsync(CancellationToken cancellationToken) =>
        ReleaseEntry.ParseGitHub(await DownloadAsync(releases, MaxList, cancellationToken).ConfigureAwait(false), downloads);

    public async Task<byte[]> DownloadAsync(Uri url, int maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw new InvalidDataException($"{url.AbsolutePath}: больше {maxBytes} байт");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    throw new InvalidDataException($"{url.AbsolutePath}: больше {maxBytes} байт");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }
}
