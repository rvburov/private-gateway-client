using System.Net;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Infrastructure.Import;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Загрузка подписки без сети: ответы подставляет обработчик HTTP.</summary>
public sealed class SubscriptionDownloaderTests
{
    private static readonly Uri Url = new("https://sub.example.com/api/v1/client/subscribe?token=fake-token-1");

    private readonly FakeTimeProvider _time = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Body_comes_back_as_utf8_text()
    {
        var downloader = Downloader(_ => Ok("vless://… #Нидерланды"));

        (await downloader.DownloadAsync(Url, Ct)).ShouldBe("vless://… #Нидерланды");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Error_status_is_reported_with_its_code_and_without_the_address(HttpStatusCode status)
    {
        var downloader = Downloader(_ => new HttpResponseMessage(status));

        var e = await Should.ThrowAsync<SubscriptionDownloadException>(() => downloader.DownloadAsync(Url, Ct));

        e.Failure.ShouldBe(DownloadFailure.Status);
        e.StatusCode.ShouldBe((int)status);
        e.ToString().ShouldNotContain("fake-token-1");
    }

    [Fact]
    public async Task Declared_size_over_the_limit_is_refused_before_reading()
    {
        var downloader = Downloader(_ =>
        {
            var response = Ok(string.Empty);
            response.Content.Headers.ContentLength = HttpSubscriptionDownloader.MaxBytes + 1L;
            return response;
        });

        (await Should.ThrowAsync<SubscriptionDownloadException>(() => downloader.DownloadAsync(Url, Ct)))
            .Failure.ShouldBe(DownloadFailure.TooLarge);
    }

    [Fact]
    public async Task Body_over_the_limit_without_a_declared_size_is_cut_off()
    {
        var body = new byte[HttpSubscriptionDownloader.MaxBytes + 1];
        var downloader = Downloader(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(body)) });

        (await Should.ThrowAsync<SubscriptionDownloadException>(() => downloader.DownloadAsync(Url, Ct)))
            .Failure.ShouldBe(DownloadFailure.TooLarge);
    }

    [Fact]
    public async Task Body_of_exactly_the_limit_is_accepted()
    {
        var body = Encoding.ASCII.GetBytes(new string('a', HttpSubscriptionDownloader.MaxBytes));
        var downloader = Downloader(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(body)) });

        (await downloader.DownloadAsync(Url, Ct)).Length.ShouldBe(HttpSubscriptionDownloader.MaxBytes);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, DownloadFailure.NameResolution)]
    [InlineData(HttpRequestError.ConnectionError, DownloadFailure.Connection)]
    [InlineData(HttpRequestError.SecureConnectionError, DownloadFailure.Tls)]
    [InlineData(HttpRequestError.Unknown, DownloadFailure.Network)]
    public async Task Network_errors_keep_only_the_reason(HttpRequestError error, DownloadFailure expected)
    {
        var downloader = Downloader(_ => throw new HttpRequestException(error, "Ошибка для " + Url));

        var e = await Should.ThrowAsync<SubscriptionDownloadException>(() => downloader.DownloadAsync(Url, Ct));

        e.Failure.ShouldBe(expected);
        e.Message.ShouldNotContain("fake-token-1");
    }

    [Fact]
    public async Task Silent_server_turns_into_a_timeout()
    {
        var downloader = new HttpSubscriptionDownloader(new HttpClient(new HangingHandler()), _time);

        var download = downloader.DownloadAsync(Url, Ct);
        _time.Advance(HttpSubscriptionDownloader.Timeout - TimeSpan.FromSeconds(1));
        download.IsCompleted.ShouldBeFalse();
        _time.Advance(TimeSpan.FromSeconds(1));

        // Срок — по часам программы: без них ответа пришлось бы ждать настоящие 20 секунд.
        (await Should.ThrowAsync<SubscriptionDownloadException>(() => download.WaitAsync(TimeSpan.FromSeconds(5), Ct)))
            .Failure.ShouldBe(DownloadFailure.Timeout);
    }

    [Fact]
    public async Task Cancellation_by_the_caller_is_not_a_timeout()
    {
        var downloader = new HttpSubscriptionDownloader(new HttpClient(new HangingHandler()), _time);
        using var cancel = new CancellationTokenSource();

        var download = downloader.DownloadAsync(Url, cancel.Token);
        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => download);
    }

    [Fact]
    public async Task Only_http_addresses_are_downloaded()
    {
        var downloader = Downloader(_ => Ok("never"));

        await Should.ThrowAsync<ArgumentException>(() => downloader.DownloadAsync(new Uri("file:///etc/passwd"), Ct));
    }

    private HttpSubscriptionDownloader Downloader(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(respond)), _time);

    private static HttpResponseMessage Ok(string text) =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    /// <summary>Сервер принял соединение и молчит.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("недостижимо");
        }
    }
}
