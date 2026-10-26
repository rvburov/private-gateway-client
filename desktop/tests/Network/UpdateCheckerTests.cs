using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Grpc.Core;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Core.Updates;
using PrivateGateway.Client.Infrastructure.Service;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Updates;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Проверка обновлений в службе (<see cref="UpdateChecker"/>, <see cref="GitHubReleaseFeed"/>) по настоящему HTTP на
/// 127.0.0.1 — как у GitHub: список выпусков, загрузки с переадресацией в хранилище — и вызов CheckForUpdate через IPC.
/// </summary>
public sealed class UpdateCheckerTests : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));
    private readonly ServiceJournal _journal;
    private readonly ReleaseSite _site = new();
    private readonly HttpClient _http = GitHubReleaseFeed.CreateHttp();
    private readonly string _state = Directory.CreateTempSubdirectory("pg-update-").FullName;

    public UpdateCheckerTests() => _journal = new ServiceJournal(_time);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _key.Dispose();
        _site.Dispose();
        _http.Dispose();
        Directory.Delete(_state, recursive: true);
    }

    [Fact]
    public async Task Signed_release_is_found_through_redirects_and_remembered_for_an_hour()
    {
        _site.Release("0.3.0", Manifest("0.3.0"), Sign(Manifest("0.3.0")));
        using var checker = Checker("0.2.0");

        var offer = await checker.CheckAsync(UpdateChannel.Stable, force: false, Ct);

        offer.State.ShouldBe(UpdateState.Available);
        (offer.Version, offer.Checked).ShouldBe(("0.3.0", _time.GetUtcNow()));
        checker.Found!.Assets["win-x64-msi"].File.ShouldBe("PrivateGateway-0.3.0-win-x64.msi");
        _site.Redirects.ShouldBe(2, "манифест и подпись — через переадресацию, как у GitHub");
        _journal.Tail(10).Count(e => e.Message.Contains("Доступно обновление 0.3.0", StringComparison.Ordinal)).ShouldBe(1);

        var requests = _site.Requests;
        (await checker.CheckAsync(UpdateChannel.Stable, force: false, Ct)).ShouldBe(offer);
        _site.Requests.ShouldBe(requests, "час служба отвечает по памяти");
        (await checker.CheckAsync(UpdateChannel.Beta, force: false, Ct)).State.ShouldBe(UpdateState.Available);
        _site.Requests.ShouldBeGreaterThan(requests, "другой канал — другой список");

        requests = _site.Requests;
        await checker.CheckAsync(UpdateChannel.Beta, force: true, Ct);
        _site.Requests.ShouldBeGreaterThan(requests, "«Проверить» — мимо памяти");
        requests = _site.Requests;
        _time.Advance(TimeSpan.FromMinutes(61));
        await checker.CheckAsync(UpdateChannel.Beta, force: false, Ct);
        _site.Requests.ShouldBeGreaterThan(requests, "через час — заново");
        _journal.Tail(20).Count(e => e.Message.Contains("Доступно обновление", StringComparison.Ordinal)).ShouldBe(1, "одна и та же версия в журнал — один раз");
    }

    [Fact]
    public async Task Failure_goes_to_the_journal_and_is_not_remembered()
    {
        _site.ListStatus = HttpStatusCode.Forbidden;
        using var checker = Checker("0.2.0");

        (await checker.CheckAsync(UpdateChannel.Stable, force: false, Ct)).State.ShouldBe(UpdateState.Failed);
        _journal.Tail(5).ShouldContain(e => e.Level == JournalLevel.Warning && e.Message.StartsWith("Проверить обновления не удалось", StringComparison.Ordinal));

        _site.ListStatus = HttpStatusCode.OK;
        _site.Release("0.3.0", Manifest("0.3.0"), Sign(Manifest("0.3.0")));
        (await checker.CheckAsync(UpdateChannel.Stable, force: false, Ct)).State.ShouldBe(UpdateState.Available, "неудача не запоминается");
    }

    [Fact]
    public async Task Oversized_manifest_is_not_read_to_the_end()
    {
        var huge = new byte[UpdateManifest.MaxBytes + 1];
        _site.Release("0.3.0", huge, "c2lnbmF0dXJl\n", chunked: true);
        using var checker = Checker("0.2.0");

        (await checker.CheckAsync(UpdateChannel.Stable, force: false, Ct)).State.ShouldBe(UpdateState.Failed);
        _journal.Tail(5).ShouldContain(e => e.Message.Contains("больше", StringComparison.Ordinal), "даже без длины в заголовках");
    }

    [Fact]
    public async Task Skipped_releases_are_named_in_the_journal()
    {
        _site.Release("0.3.0", Manifest("0.3.0"), Sign(Manifest("0.2.9")));
        using var checker = Checker("0.2.0");

        (await checker.CheckAsync(UpdateChannel.Stable, force: false, Ct)).State.ShouldBe(UpdateState.UpToDate);
        _journal.Tail(5).ShouldContain(e => e.Message == "Выпуск пропущен: 0.3.0: подпись манифеста не сходится с ключом программы");
    }

    [Fact]
    public void Install_number_is_drawn_once_and_kept()
    {
        var first = UpdateChecker.LoadBucket(_state);

        first.ShouldBeInRange(0, 99);
        UpdateChecker.LoadBucket(_state).ShouldBe(first);
        File.ReadAllText(Path.Combine(_state, "update-bucket")).ShouldBe(first.ToString(CultureInfo.InvariantCulture));
        File.WriteAllText(Path.Combine(_state, "update-bucket"), "250");
        UpdateChecker.LoadBucket(_state).ShouldBeInRange(0, 99, "испорченный номер — новый");
    }

    [Fact]
    public async Task Window_asks_through_ipc_with_its_channel()
    {
        _site.Release("0.4.0-beta.1", Manifest("0.4.0-beta.1", "beta"), Sign(Manifest("0.4.0-beta.1", "beta")), prerelease: true);
        _site.Release("0.3.0", Manifest("0.3.0"), Sign(Manifest("0.3.0")));
        await using var service = await RunningService.StartAsync(updates: journal =>
            new UpdateChecker(Feed(), [_key.ExportSubjectPublicKeyInfoPem()], "0.2.0", 0, _time, journal));
        using var client = new ServiceGatewayClient(IpcChannel.Create(service.Endpoint, system: false), ServiceKind.User, _ => null, () => AppSettings.Default, TimeProvider.System);

        (await client.CheckForUpdateAsync(UpdateChannel.Stable, force: false, Ct))!.Version.ShouldBe("0.3.0");
        var beta = await client.CheckForUpdateAsync(UpdateChannel.Beta, force: false, Ct);
        beta!.Version.ShouldBe("0.4.0-beta.1");
        beta.Notes.ShouldBe(new Uri("https://github.com/rvburov/private-gateway-client/releases/tag/v0.4.0-beta.1"));
        var refused = await Should.ThrowAsync<RpcException>(async () =>
            await service.Client.CheckForUpdateAsync(new V1.CheckForUpdateRequest(), cancellationToken: Ct));
        refused.StatusCode.ShouldBe(StatusCode.InvalidArgument, "канал не задан — служба не угадывает");
    }

    [Fact]
    public async Task Service_without_keys_says_so_without_the_network()
    {
        await using var service = await RunningService.StartAsync();

        var offer = await service.Client.CheckForUpdateAsync(new V1.CheckForUpdateRequest { Channel = V1.UpdateChannel.Stable }, cancellationToken: Ct);

        offer.State.ShouldBe(V1.UpdateState.NotSigned);
    }

    private UpdateChecker Checker(string current) => new(Feed(), [_key.ExportSubjectPublicKeyInfoPem()], current, 0, _time, _journal);

    private GitHubReleaseFeed Feed() => new(_http, new Uri(_site.Url, "repos/releases"), new Uri(_site.Url, "download/"));

    private static byte[] Manifest(string version, string channel = "stable") => Encoding.UTF8.GetBytes($$"""
        {"version":"{{version}}","channel":"{{channel}}","rollout":100,"published":"2026-10-26T08:00:00Z",
         "notes":"https://github.com/rvburov/private-gateway-client/releases/tag/v{{version}}","highlights":["Новое"],
         "assets":{"win-x64-msi":{"file":"PrivateGateway-{{version}}-win-x64.msi","sha256":"{{new string('a', 64)}}","size":10} } }
        """);

    private string Sign(byte[] manifest) =>
        Convert.ToBase64String(_key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) + "\n";

    /// <summary>
    /// «GitHub» на 127.0.0.1: /repos/releases — список выпусков (ответ API), /download/v…/файл — переадресация 302 на
    /// /storage/v…/файл, как загрузки выпусков; chunked — без длины в заголовках.
    /// </summary>
    private sealed class ReleaseSite : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly List<(string Version, bool Prerelease)> _releases = [];
        private readonly Dictionary<string, (byte[] Body, bool Chunked)> _files = new(StringComparer.Ordinal);
        private int _requests;
        private int _redirects;

        public ReleaseSite()
        {
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public Uri Url => new($"http://{(IPEndPoint)_listener.LocalEndpoint}/");

        public HttpStatusCode ListStatus { get; set; } = HttpStatusCode.OK;

        public int Requests => Volatile.Read(ref _requests);

        public int Redirects => Volatile.Read(ref _redirects);

        public void Release(string version, byte[] manifest, string signature, bool prerelease = false, bool chunked = false)
        {
            lock (_files)
            {
                _releases.Add((version, prerelease));
                _files[$"/storage/v{version}/update.json"] = (manifest, chunked);
                _files[$"/storage/v{version}/update.json.sig"] = (Encoding.ASCII.GetBytes(signature), false);
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }

                using (client)
                {
                    var stream = client.GetStream();
                    var request = new StringBuilder();
                    var buffer = new byte[4096];
                    while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                        if (read == 0)
                        {
                            break;
                        }

                        request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    Interlocked.Increment(ref _requests);
                    var path = request.ToString().Split(' ')[1];
                    try
                    {
                        await stream.WriteAsync(Respond(path)).ConfigureAwait(false);
                    }
                    catch (IOException)
                    {
                        // Клиент бросил чтение (файл больше предела).
                    }
                }
            }
        }

        private byte[] Respond(string path)
        {
            lock (_files)
            {
                if (path.StartsWith("/repos/releases", StringComparison.Ordinal))
                {
                    if (ListStatus != HttpStatusCode.OK)
                    {
                        return Response($"{(int)ListStatus} Error", "{\"message\":\"API rate limit exceeded\"}"u8.ToArray());
                    }

                    var list = string.Join(',', _releases.Select(r => $$"""
                        {"tag_name":"v{{r.Version}}","draft":false,"prerelease":{{(r.Prerelease ? "true" : "false")}},"assets":[
                          {"name":"update.json","browser_download_url":"{{Url}}download/v{{r.Version}}/update.json"},
                          {"name":"update.json.sig","browser_download_url":"{{Url}}download/v{{r.Version}}/update.json.sig"}]}
                        """));
                    return Response("200 OK", Encoding.UTF8.GetBytes($"[{list}]"));
                }

                if (path.StartsWith("/download/", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _redirects);
                    return Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: /storage/{path["/download/".Length..]}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                }

                return _files.TryGetValue(path, out var file) ? Response("200 OK", file.Body, file.Chunked) : Response("404 Not Found", []);
            }
        }

        private static byte[] Response(string status, byte[] body, bool chunked = false) => chunked
            ? [.. Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n{body.Length:x}\r\n"), .. body, .. "\r\n0\r\n\r\n"u8]
            : [.. Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), .. body];
    }
}
