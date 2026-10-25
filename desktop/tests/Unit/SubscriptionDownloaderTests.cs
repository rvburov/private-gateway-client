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

        (await downloader.DownloadAsync(Url, Ct)).ShouldBe(new SubscriptionContent("vless://… #Нидерланды", null));
    }

    [Fact]
    public async Task Profile_title_names_the_subscription()
    {
        var downloader = Downloader(_ =>
        {
            var response = Ok("vless://…");
            // Так присылает PRIVATE GATEWAY: имя устройства в base64.
            response.Headers.Add("Profile-Title", "base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Ноутбук & «дом»")));
            return response;
        });

        (await downloader.DownloadAsync(Url, Ct)).Title.ShouldBe("Ноутбук & «дом»");
    }

    [Theory]
    [InlineData("Office VPN", "Office VPN")]
    [InlineData("  base64:0J3QvtGD0YLQsdGD0Lo=  ", "Ноутбук")]
    [InlineData("BASE64:0J3QvtGD0YLQsdGD0Lo", "Ноутбук")]
    [InlineData("base64:not base64!", null)]
    [InlineData("base64:AQID", null)]
    [InlineData("CafÃ©", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Profile_title_is_base64_utf8_or_plain_ascii(string? header, string? title)
    {
        SubscriptionContent.TitleFrom(header).ShouldBe(title);
    }

    [Fact]
    public void Long_profile_title_is_cut()
    {
        SubscriptionContent.TitleFrom(new string('a', 150)).ShouldBe(new string('a', 100));
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

        (await downloader.DownloadAsync(Url, Ct)).Text.Length.ShouldBe(HttpSubscriptionDownloader.MaxBytes);
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
