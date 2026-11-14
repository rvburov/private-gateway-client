using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PrivateGateway.Client.Application.Account;
using PrivateGateway.Client.Core.Account;
using PrivateGateway.Client.Infrastructure.Account;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Два настоящих HTTP origin на loopback: автоматический 307/308 перенес бы пароль на второй порт.</summary>
public sealed class AccountRedirectTests
{
    [Theory]
    [InlineData(307, false)]
    [InlineData(308, false)]
    [InlineData(307, true)]
    [InlineData(308, true)]
    public async Task Production_clients_never_follow_a_login_redirect_with_password_and_codes(int status, bool direct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var ct = deadline.Token;
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        using var destination = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        destination.Start();
        var redirected = 0;
        var redirectUrl = Address(destination) + "unexpected";
        var first = Answer(origin, status, redirectUrl, ct);
        var second = Task.Run(async () =>
        {
            try
            {
                await Answer(destination, 400, null, ct, () => Interlocked.Increment(ref redirected));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Защищенный клиент сюда не обращается: прием прекращается вместе с тестом.
            }
        }, CancellationToken.None);
        try
        {
            using var http = direct ? HttpAccountApi.CreateDirectClient() : HttpAccountApi.CreateClient();
            var api = new HttpAccountApi(http, TimeProvider.System, Address(origin));
            var device = new DeviceInfo("11111111-2222-3333-4444-555555555555", "TEST", "macos", "0.3.0");

            var failure = await Should.ThrowAsync<AccountApiException>(() => api.LoginAsync(
                new LoginRequest("user@example.com", "password-marker", "123456", "recovery-marker", device), ct));

            (failure.Failure, failure.StatusCode).ShouldBe((AccountFailure.Server, status));
            failure.Message.ShouldNotContain("password-marker");
            var body = await first;
            body.ShouldContain("password-marker");
            body.ShouldContain("123456");
            body.ShouldContain("recovery-marker");
            redirected.ShouldBe(0, "ни пароль, ни код, ни резервный код не отправлены другому origin");
        }
        finally
        {
            await deadline.CancelAsync();
            await second;
        }
    }

    private static Uri Address(TcpListener listener) => new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");

    private static async Task<string> Answer(TcpListener listener, int status, string? location, CancellationToken ct, Action? accepted = null)
    {
        using var client = await listener.AcceptTcpClientAsync(ct);
        accepted?.Invoke();
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var length = 0;
        while (await reader.ReadLineAsync(ct) is { Length: > 0 } line)
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                length = int.Parse(line["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture);
            }
        }

        var body = new char[length];
        (await reader.ReadBlockAsync(body.AsMemory(), ct)).ShouldBe(length);
        var redirect = location is null ? string.Empty : "Location: " + location + "\r\n";
        var response = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\n{redirect}Content-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, ct);
        return new string(body);
    }
}
