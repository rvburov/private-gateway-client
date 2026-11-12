using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PrivateGateway.Client.Application.Account;
using PrivateGateway.Client.Core.Account;

namespace PrivateGateway.Client.Infrastructure.Account;

/// <summary>
/// API входа сайта по https (<see cref="AccountSite.Address"/>): вход устройства, проверка доступа, выход. Каждый
/// запрос — не дольше <see cref="Timeout"/> по часам программы, ответ — не больше <see cref="MaxBytes"/>. Токен — только
/// в заголовке Authorization; ни токен, ни пароль, ни коды, ни почта, ни тело ответа в ошибки не попадают.
/// Как и загрузка подписки: сначала через системный прокси, а если до него не дойти и он чужой
/// (<paramref name="mayBypass"/>) — еще раз напрямую (<paramref name="direct"/>): прокси часто оставляет другой
/// VPN-клиент, который сейчас выключен.
/// </summary>
/// <param name="http">Клиент с системным прокси.</param>
/// <param name="time">Часы для предела времени и <c>Retry-After</c> с датой.</param>
/// <param name="site">Адрес сайта; null — <see cref="AccountSite.Address"/>. Только https; http — только к своему
/// компьютеру (тесты).</param>
/// <param name="direct">Клиент без прокси для второй попытки; null — без нее.</param>
/// <param name="mayBypass">Можно ли идти мимо системного прокси для этого адреса; null — можно.</param>
public sealed class HttpAccountApi(HttpClient http, TimeProvider time, Uri? site = null, HttpClient? direct = null, Func<Uri, bool>? mayBypass = null)
    : IAccountApi
{
    /// <summary>Сколько ждать ответа сайта.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Больше этого ответ сайта не читается: там несколько полей.</summary>
    public const int MaxBytes = 64 * 1024;

    /// <summary>Предел имени устройства и версии — как у сайта.</summary>
    public const int MaxDeviceName = 100;

    public const int MaxAppVersion = 64;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Uri _site = Checked(site ?? AccountSite.Address);

    /// <summary>Клиент для программы: системный прокси, как у браузера.</summary>
    public static HttpClient CreateClient() => new(new SocketsHttpHandler());

    /// <summary>То же без системного прокси — вторая попытка, когда прокси не отвечает.</summary>
    public static HttpClient CreateDirectClient() => new(new SocketsHttpHandler { UseProxy = false });

    public async Task<AccountLogin> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var body = new LoginBody(request.Email, request.Password, request.Code, request.RecoveryCode,
            new DeviceBody(request.Device.Uid, Cut(request.Device.Name, MaxDeviceName), request.Device.Platform,
                Cut(request.Device.AppVersion, MaxAppVersion)));
        var reply = await SendAsync<LoginReply>(HttpMethod.Post, "api/app/login", null, body, cancellationToken).ConfigureAwait(false);
        if (reply is not { Token: { Length: > 0 } token })
        {
            throw new AccountApiException(AccountFailure.Server, 200);
        }

        return new AccountLogin(token, StatusOf(reply.ExpiresAt, reply.User, reply.Device));
    }

    public async Task<AccountStatus> StatusAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        var reply = await SendAsync<StatusReply>(HttpMethod.Get, "api/app/status", token, null, cancellationToken).ConfigureAwait(false);
        if (reply is not { Status: "ok" })
        {
            throw new AccountApiException(AccountFailure.Server, 200);
        }

        return StatusOf(reply.ExpiresAt, reply.User, reply.Device);
    }

    public async Task LogoutAsync(string token, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        try
        {
            await SendAsync<object>(HttpMethod.Post, "api/app/logout", token, null, cancellationToken).ConfigureAwait(false);
        }
        catch (AccountApiException e) when (e.Failure == AccountFailure.InvalidCredentials)
        {
            // Токен уже не действует — выходить не из чего.
        }
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string path, string? token, object? body, CancellationToken cancellationToken)
        where T : class
    {
        var url = new Uri(_site, path);
        using var timeout = new CancellationTokenSource(Timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            try
            {
                return await FetchAsync<T>(http, method, url, token, body, linked.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException e) when (direct is not null && e.HttpRequestError is HttpRequestError.ProxyTunnelError or HttpRequestError.ConnectionError
                && (mayBypass?.Invoke(url) ?? true))
            {
                return await FetchAsync<T>(direct, method, url, token, body, linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AccountApiException(AccountFailure.Network, innerException: e);
        }
        catch (HttpRequestException e)
        {
            // Текст исключения HTTP наружу не идет: только причина.
            throw new AccountApiException(AccountFailure.Network, innerException: e);
        }
        catch (Exception e) when (e is IOException or AuthenticationException or SocketException)
        {
            // Связь оборвалась после заголовков, пока читалось тело (HttpIOException — это IOException, а не
            // HttpRequestException): та же причина «нет связи», исключение из входа не выходит.
            throw new AccountApiException(AccountFailure.Network, innerException: e);
        }
    }

    private async Task<T?> FetchAsync<T>(HttpClient client, HttpMethod method, Uri url, string? token, object? body, CancellationToken cancellationToken)
        where T : class
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.UserAgent.ParseAdd("PrivateGateway/0.1");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), Json), Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (!response.IsSuccessStatusCode)
        {
            throw Failure(response, status);
        }

        if (typeof(T) == typeof(object) || response.StatusCode == HttpStatusCode.NoContent)
        {
            return null;
        }

        var bytes = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, Json);
        }
        catch (JsonException e)
        {
            throw new AccountApiException(AccountFailure.Server, status, innerException: e);
        }
    }

    // Причина — по коду ответа; тело ответа не читается: оно ничего не добавляет к коду и могло бы попасть в журнал.
    private AccountApiException Failure(HttpResponseMessage response, int status) => status switch
    {
        401 => new AccountApiException(AccountFailure.InvalidCredentials, status),
        400 => new AccountApiException(AccountFailure.InvalidCode, status),
        403 => new AccountApiException(AccountFailure.SetupRequired, status),
        409 => new AccountApiException(AccountFailure.DeviceLimit, status),
        422 => new AccountApiException(AccountFailure.Validation, status),
        429 => new AccountApiException(AccountFailure.RateLimited, status,
            response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - time.GetUtcNow())),
        _ => new AccountApiException(AccountFailure.Server, status),
    };

    private static async Task<byte[]> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaxBytes)
        {
            throw new AccountApiException(AccountFailure.Server, (int)response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                throw new AccountApiException(AccountFailure.Server, (int)response.StatusCode);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static AccountStatus StatusOf(DateTimeOffset? expiresAt, UserReply? user, DeviceReply? device) =>
        expiresAt is { } expires && user is { Email: { Length: > 0 } email }
            ? new AccountStatus(expires, new AccountUser(email, string.IsNullOrWhiteSpace(user.DisplayName) ? null : user.DisplayName),
                new AccountDevice(device?.Name ?? ""))
            : throw new AccountApiException(AccountFailure.Server, 200);

    private static Uri Checked(Uri site)
    {
        ArgumentNullException.ThrowIfNull(site);
        if (!site.IsAbsoluteUri || !(site.Scheme == Uri.UriSchemeHttps || (site.Scheme == Uri.UriSchemeHttp && site.IsLoopback)))
        {
            throw new ArgumentException("Сайт — только по https (http — только к своему компьютеру).", nameof(site));
        }

        return site.AbsolutePath.EndsWith('/') ? site : new Uri(site.AbsoluteUri + "/");
    }

    private static string Cut(string value, int max) => value.Length > max ? value[..max] : value;

    private sealed record LoginBody(string Email, string Password, string? Code, string? RecoveryCode, DeviceBody Device);

    private sealed record DeviceBody(string Uid, string Name, string Platform, string AppVersion);

    private sealed record LoginReply(string? Token, DateTimeOffset? ExpiresAt, UserReply? User, DeviceReply? Device);

    private sealed record StatusReply(string? Status, DateTimeOffset? ExpiresAt, UserReply? User, DeviceReply? Device);

    private sealed record UserReply(string? Email, string? DisplayName);

    private sealed record DeviceReply(string? Name);
}
