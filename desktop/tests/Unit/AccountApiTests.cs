using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Account;
using PrivateGateway.Client.Core.Account;
using PrivateGateway.Client.Infrastructure.Account;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>API входа сайта без сети: ответы подставляет обработчик HTTP, адрес сайта — выдуманный.</summary>
public sealed class AccountApiTests
{
    private const string Password = "pw-secret-marker";
    private const string Email = "user@example.com";
    private const string Token = "fake-token-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Uid = "11111111-2222-3333-4444-555555555555";

    private static readonly Uri Site = new("https://site.example.com/");

    private static readonly DeviceInfo Device = new(Uid, "WORKSTATION", "linux", "0.2.0");

    private static readonly string LoginReply = """
        {"token":"fake-token-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","expires_at":"2026-12-12T10:00:00.123456+00:00",
         "user":{"email":"user@example.com","display_name":"Роман"},
         "device":{"id":7,"name":"WORKSTATION","platform":"linux","app_version":"0.2.0","ip_masked":"203.0.113.x",
                   "created_at":"2026-11-12T10:00:00Z","last_seen_at":"2026-11-12T10:00:00Z"}}
        """;

    private static readonly string StatusReply = """
        {"status":"ok","expires_at":"2027-01-01T00:00:00Z","user":{"email":"user@example.com","display_name":null},
         "device":{"id":7,"name":"WORKSTATION"}}
        """;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero));
    private readonly List<Sent> _sent = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Login_posts_credentials_and_device_and_reads_the_token()
    {
        var api = Api(_ => Json(HttpStatusCode.OK, LoginReply));

        var login = await api.LoginAsync(new LoginRequest(Email, Password, "123456", null, Device), Ct);

        login.Token.ShouldBe(Token);
        login.Status.ShouldBe(new AccountStatus(new DateTimeOffset(2026, 12, 12, 10, 0, 0, 123, TimeSpan.Zero).AddTicks(4560),
            new AccountUser(Email, "Роман"), new AccountDevice("WORKSTATION")));
        login.ToString().ShouldNotContain(Token, Case.Sensitive, "токен в строку записи не попадает");
        var sent = _sent.ShouldHaveSingleItem();
        (sent.Method, sent.Url, sent.Authorization, sent.ContentType).ShouldBe(("POST", "https://site.example.com/api/app/login", null, "application/json"));
        JsonNode.DeepEquals(JsonNode.Parse(sent.Body!), JsonNode.Parse("""
            {"email":"user@example.com","password":"pw-secret-marker","code":"123456",
             "device":{"uid":"11111111-2222-3333-4444-555555555555","name":"WORKSTATION","platform":"linux","app_version":"0.2.0"}}
            """)).ShouldBeTrue(sent.Body);
    }

    [Fact]
    public async Task Recovery_code_goes_instead_of_the_code_and_long_names_are_cut()
    {
        var api = Api(_ => Json(HttpStatusCode.OK, LoginReply));
        var device = Device with { Name = new string('n', 150), AppVersion = new string('v', 80) };

        await api.LoginAsync(new LoginRequest(Email, Password, null, "abcd-efgh", device), Ct);

        var body = JsonNode.Parse(_sent.Single().Body!)!.AsObject();
        body.ContainsKey("code").ShouldBeFalse("ровно одно из кода и резервного кода");
        body["recovery_code"]!.GetValue<string>().ShouldBe("abcd-efgh");
        body["device"]!["name"]!.GetValue<string>().Length.ShouldBe(HttpAccountApi.MaxDeviceName);
        body["device"]!["app_version"]!.GetValue<string>().Length.ShouldBe(HttpAccountApi.MaxAppVersion);
    }

    [Theory]
    [InlineData(401, "invalid_credentials", AccountFailure.InvalidCredentials)]
    [InlineData(400, "invalid_code", AccountFailure.InvalidCode)]
    [InlineData(403, "setup_required", AccountFailure.SetupRequired)]
    [InlineData(409, "device_limit", AccountFailure.DeviceLimit)]
    [InlineData(422, "validation_error", AccountFailure.Validation)]
    [InlineData(429, "rate_limited", AccountFailure.RateLimited)]
    [InlineData(500, "internal", AccountFailure.Server)]
    [InlineData(502, "bad_gateway", AccountFailure.Server)]
    [InlineData(404, "not_found", AccountFailure.Server)]
    public async Task Every_refusal_of_the_site_has_its_reason_and_no_secrets(int status, string code, AccountFailure failure)
    {
        var api = Api(_ => Json((HttpStatusCode)status, $$$"""{"error":{"code":"{{{code}}}","detail":"{{{Email}}} {{{Password}}}"}}"""));

        var error = await Should.ThrowAsync<AccountApiException>(() => api.LoginAsync(new LoginRequest(Email, Password, "123456", null, Device), Ct));

        (error.Failure, error.StatusCode).ShouldBe((failure, status));
        foreach (var secret in new[] { Email, Password, "123456", code, Uid })
        {
            error.Message.ShouldNotContain(secret, Case.Sensitive, "в тексте ошибки — только причина и код ответа");
        }
    }

    [Theory]
    [InlineData("90", 90, 2)]
    [InlineData("1", 1, 1)]
    [InlineData("600", 600, 10)]
    public async Task Too_many_tries_say_how_long_to_wait(string retryAfter, int seconds, int minutes)
    {
        var api = Api(_ =>
        {
            var response = Json(HttpStatusCode.TooManyRequests, """{"error":{"code":"rate_limited"}}""");
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return response;
        });

        var error = await Should.ThrowAsync<AccountApiException>(() => api.LoginAsync(new LoginRequest(Email, Password, "123456", null, Device), Ct));

        error.RetryAfter.ShouldBe(TimeSpan.FromSeconds(seconds));
        LoginOutcome.Failed(error.Failure, error.RetryAfter).ShouldBe(new LoginOutcome(AccountFailure.RateLimited, minutes),
            "минуты — вверх, не меньше одной");
    }

    [Fact]
    public void Rate_limit_without_a_header_is_still_at_least_a_minute() =>
        LoginOutcome.Failed(AccountFailure.RateLimited).RetryAfterMinutes.ShouldBe(1);

    [Fact]
    public async Task Status_sends_the_token_only_in_the_header_and_reads_the_new_term()
    {
        var api = Api(_ => Json(HttpStatusCode.OK, StatusReply));

        var status = await api.StatusAsync(Token, Ct);

        status.ShouldBe(new AccountStatus(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), new AccountUser(Email, null), new AccountDevice("WORKSTATION")));
        var sent = _sent.ShouldHaveSingleItem();
        (sent.Method, sent.Url, sent.Authorization, sent.Body).ShouldBe(("GET", "https://site.example.com/api/app/status", "Bearer " + Token, null));
    }

    [Fact]
    public async Task Status_401_means_access_is_revoked()
    {
        var api = Api(_ => Json(HttpStatusCode.Unauthorized, """{"error":{"code":"unauthorized"}}"""));

        var error = await Should.ThrowAsync<AccountApiException>(() => api.StatusAsync(Token, Ct));

        error.Failure.ShouldBe(AccountFailure.InvalidCredentials);
        error.Message.ShouldNotContain(Token);
    }

    [Theory]
    [InlineData("<html>captive portal</html>")]
    [InlineData("""{"status":"blocked","expires_at":"2027-01-01T00:00:00Z","user":{"email":"user@example.com"}}""")]
    [InlineData("""{"status":"ok","user":{"email":"user@example.com"}}""")]
    [InlineData("""{"status":"ok","expires_at":"2027-01-01T00:00:00Z"}""")]
    [InlineData("")]
    public async Task Unexpected_answer_is_a_server_failure_not_a_revocation(string body)
    {
        var api = Api(_ => Json(HttpStatusCode.OK, body));

        (await Should.ThrowAsync<AccountApiException>(() => api.StatusAsync(Token, Ct))).Failure.ShouldBe(AccountFailure.Server);
    }

    [Fact]
    public async Task Login_without_a_token_in_the_answer_is_a_server_failure()
    {
        var api = Api(_ => Json(HttpStatusCode.OK, """{"expires_at":"2027-01-01T00:00:00Z","user":{"email":"user@example.com"}}"""));

        (await Should.ThrowAsync<AccountApiException>(() => api.LoginAsync(new LoginRequest(Email, Password, "123456", null, Device), Ct)))
            .Failure.ShouldBe(AccountFailure.Server);
    }

    [Fact]
    public async Task Oversized_answer_is_not_read_to_the_end()
    {
        var api = Api(_ => Json(HttpStatusCode.OK, "{\"status\":\"ok\",\"pad\":\"" + new string('x', HttpAccountApi.MaxBytes) + "\"}"));

        (await Should.ThrowAsync<AccountApiException>(() => api.StatusAsync(Token, Ct))).Failure.ShouldBe(AccountFailure.Server);
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task Logout_posts_the_token_and_an_already_dead_token_is_fine(HttpStatusCode status)
    {
        var api = Api(_ => new HttpResponseMessage(status));

        await api.LogoutAsync(Token, Ct);

        var sent = _sent.ShouldHaveSingleItem();
        (sent.Method, sent.Url, sent.Authorization).ShouldBe(("POST", "https://site.example.com/api/app/logout", "Bearer " + Token));
    }

    [Fact]
    public async Task Logout_failure_of_the_site_is_reported()
    {
        var api = Api(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        (await Should.ThrowAsync<AccountApiException>(() => api.LogoutAsync(Token, Ct))).Failure.ShouldBe(AccountFailure.Server);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    [InlineData(HttpRequestError.ConnectionError)]
    public async Task No_connection_is_a_network_failure_without_the_text_of_the_error(HttpRequestError reason)
    {
        var api = new HttpAccountApi(new HttpClient(new StubHandler(_ => throw new HttpRequestException(reason, "detail " + Token))), _time, Site);

        var error = await Should.ThrowAsync<AccountApiException>(() => api.StatusAsync(Token, Ct));

        (error.Failure, error.StatusCode).ShouldBe((AccountFailure.Network, null));
        error.Message.ShouldNotContain(Token);
    }

    [Fact]
    public async Task Silent_site_is_a_network_failure_after_the_timeout()
    {
        var api = new HttpAccountApi(new HttpClient(new HangingHandler()), _time, Site);

        var check = api.StatusAsync(Token, Ct);
        _time.Advance(HttpAccountApi.Timeout - TimeSpan.FromSeconds(1));
        await Task.Delay(50, Ct);
        check.IsCompleted.ShouldBeFalse("срок ожидания — по часам программы");
        _time.Advance(TimeSpan.FromSeconds(1));

        // Срок вышел по часам программы — ответ сразу, а не через настоящие 20 секунд.
        (await Should.ThrowAsync<AccountApiException>(() => check.WaitAsync(TimeSpan.FromSeconds(5), Ct))).Failure.ShouldBe(AccountFailure.Network);
    }

    [Fact]
    public async Task Cancellation_by_the_caller_stays_a_cancellation()
    {
        using var cancel = new CancellationTokenSource();
        var api = new HttpAccountApi(new HttpClient(new HangingHandler()), _time, Site);

        var check = api.StatusAsync(Token, cancel.Token);
        await cancel.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => check);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task Dead_foreign_system_proxy_gives_way_to_a_direct_try(bool foreign, int directTries)
    {
        var direct = 0;
        var api = new HttpAccountApi(new HttpClient(new StubHandler(_ => throw new HttpRequestException(HttpRequestError.ProxyTunnelError, "proxy"))),
            _time, Site, new HttpClient(new StubHandler(_ =>
            {
                direct++;
                return Json(HttpStatusCode.OK, StatusReply);
            })), _ => foreign);

        if (foreign)
        {
            (await api.StatusAsync(Token, Ct)).User.Email.ShouldBe(Email);
        }
        else
        {
            (await Should.ThrowAsync<AccountApiException>(() => api.StatusAsync(Token, Ct))).Failure.ShouldBe(AccountFailure.Network,
                "прокси своего ядра — режим прокси: мимо него запрос ушел бы мимо защиты");
        }

        direct.ShouldBe(directTries);
    }

    [Theory]
    [InlineData("http://site.example.com/")]
    [InlineData("ftp://site.example.com/")]
    public void Only_https_is_allowed_for_a_real_site(string address) =>
        Should.Throw<ArgumentException>(() => new HttpAccountApi(new HttpClient(), _time, new Uri(address)));

    [Fact]
    public async Task Http_to_this_computer_is_allowed_for_tests_and_the_default_is_the_site()
    {
        var api = new HttpAccountApi(new HttpClient(new StubHandler(r =>
        {
            _sent.Add(new Sent(r.Method.Method, r.RequestUri!.AbsoluteUri, null, null, null));
            return Json(HttpStatusCode.OK, StatusReply);
        })), _time, new Uri("http://127.0.0.1:8080/base"));

        await api.StatusAsync(Token, Ct);

        _sent.Single().Url.ShouldBe("http://127.0.0.1:8080/base/api/app/status");
        AccountSite.Address.Scheme.ShouldBe("https");
        AccountSite.DevicesPage.AbsoluteUri.ShouldBe("https://private-gateway.duckdns.org/devices");
        AccountSite.LoginPage.AbsoluteUri.ShouldBe("https://private-gateway.duckdns.org/login");
    }

    private HttpAccountApi Api(Func<HttpRequestMessage, HttpResponseMessage> respond) =>
        new(new HttpClient(new StubHandler(request =>
        {
            _sent.Add(new Sent(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(),
                request.Content?.ReadAsStringAsync().GetAwaiter().GetResult(), request.Content?.Headers.ContentType?.MediaType));
            return respond(request);
        })), _time, Site);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed record Sent(string Method, string Url, string? Authorization, string? Body, string? ContentType);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    /// <summary>Сайт принял соединение и молчит.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("недостижимо");
        }
    }
}
