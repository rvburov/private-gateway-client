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

        var stable = await client.CheckForUpdateAsync(UpdateChannel.Stable, force: false, Ct);
        stable!.Version.ShouldBe("0.3.0");
        stable.Installable.ShouldBeFalse("служба без установщика не ставит — окно ведет на страницу выпуска");
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

    [Fact]
    public async Task Update_file_is_downloaded_with_progress_checked_against_the_manifest_and_handed_to_the_installer()
    {
        var package = RandomNumberGenerator.GetBytes(300_000);
        var manifest = UpdateManifest.Parse(ManifestWith("0.3.0", "linux-x64-deb", "private-gateway_0.3.0_amd64.deb", package));
        _site.Asset("0.3.0", "private-gateway_0.3.0_amd64.deb", package);
        var launched = new List<(string File, string Log)>();
        var installer = Installer(new UpdateKind("linux-x64-deb", (file, log) =>
        {
            launched.Add((file, log));
            return 4242;
        }));
        Directory.CreateDirectory(Path.Combine(_state, "updates"));
        File.WriteAllText(Path.Combine(_state, "updates", "private-gateway_0.2.0_amd64.deb"), "прежняя загрузка");

        var progress = await Collect(installer.DownloadAsync(manifest, Ct));

        progress[^1].ShouldBe(new UpdateProgress(package.Length, package.Length, Ready: true));
        progress.ShouldAllBe(p => p.Total == package.Length && p.Error == null);
        File.ReadAllBytes(Path.Combine(_state, "updates", "private-gateway_0.3.0_amd64.deb")).ShouldBe(package);
        Directory.GetFiles(Path.Combine(_state, "updates")).Select(Path.GetFileName).ShouldBe(["private-gateway_0.3.0_amd64.deb"], "прежние загрузки убраны");
        (await Collect(installer.DownloadAsync(manifest, Ct))).ShouldBe([new UpdateProgress(package.Length, package.Length, Ready: true)],
            "уже скачанное и сверенное не качается заново");

        installer.Install(manifest).ShouldBe(4242);
        launched.ShouldBe([(Path.Combine(_state, "updates", "private-gateway_0.3.0_amd64.deb"), Path.Combine(_state, "updates", "install.log"))]);
        _journal.Tail(5).ShouldContain(e => e.Message.Contains("Ставится обновление 0.3.0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task File_that_does_not_match_the_signed_manifest_is_not_kept_or_installed()
    {
        var package = RandomNumberGenerator.GetBytes(100_000);
        var manifest = UpdateManifest.Parse(ManifestWith("0.3.0", "linux-x64-deb", "private-gateway_0.3.0_amd64.deb", package));
        var tampered = package.ToArray();
        tampered[500] ^= 1;
        _site.Asset("0.3.0", "private-gateway_0.3.0_amd64.deb", tampered);
        var installer = Installer(new UpdateKind("linux-x64-deb", (_, _) => throw new InvalidOperationException("не должен запускаться")));

        var progress = await Collect(installer.DownloadAsync(manifest, Ct));

        progress[^1].Error!.ShouldContain("SHA-256");
        Directory.GetFiles(Path.Combine(_state, "updates")).ShouldBeEmpty("подмененный файл не остается ни целым, ни частью");
        Should.Throw<IOException>(() => installer.Install(manifest));

        _site.Asset("0.3.0", "private-gateway_0.3.0_amd64.deb", [.. package, 1]);
        (await Collect(installer.DownloadAsync(manifest, Ct)))[^1].Error!.ShouldContain("байт", customMessage: "длиннее манифеста — отказ");
        _site.Asset("0.3.0", "private-gateway_0.3.0_amd64.deb", [.. package, 1], chunked: true);
        (await Collect(installer.DownloadAsync(manifest, Ct)))[^1].Error!.ShouldContain("больше", customMessage: "без длины в заголовке — отказ на первом лишнем байте");

        _site.Asset("0.3.0", "private-gateway_0.3.0_amd64.deb", package);
        (await Collect(installer.DownloadAsync(manifest, Ct)))[^1].Ready.ShouldBeTrue();
        var file = Path.Combine(_state, "updates", "private-gateway_0.3.0_amd64.deb");
        File.WriteAllBytes(file, tampered);
        Should.Throw<IOException>(() => installer.Install(manifest), "файл подменили после загрузки — перед установкой сверяется заново");
        (await Collect(Installer(null).DownloadAsync(manifest, Ct)))[^1].Error.ShouldBe("в выпуске нет файла для этой установки");
    }

    [Fact]
    public async Task Window_downloads_and_installs_only_what_the_service_found()
    {
        var package = RandomNumberGenerator.GetBytes(50_000);
        var manifest = ManifestWith("0.3.0", "linux-x64-deb", "private-gateway_0.3.0_amd64.deb", package);
        _site.Release("0.3.0", manifest, Sign(manifest));
        _site.Asset("0.3.0", "private-gateway_0.3.0_amd64.deb", package);
        await using var service = await RunningService.StartAsync(
            updates: journal => new UpdateChecker(Feed(), [_key.ExportSubjectPublicKeyInfoPem()], "0.2.0", 0, _time, journal),
            installer: journal => new UpdateInstaller(_http, new Uri(_site.Url, "download/"), Path.Combine(_state, "updates"),
                new UpdateKind("linux-x64-deb", (_, _) => 4242), journal));
        using var client = new ServiceGatewayClient(IpcChannel.Create(service.Endpoint, system: false), ServiceKind.User, _ => null, () => AppSettings.Default, TimeProvider.System);

        (await Collect(client.DownloadUpdateAsync("0.3.0", Ct)))[^1].Error.ShouldNotBeNull("служба еще не находила 0.3.0 — качать нечего");
        (await client.InstallUpdateAsync("0.3.0", Ct)).ShouldBeNull();

        (await client.CheckForUpdateAsync(UpdateChannel.Stable, force: false, Ct))!.Installable.ShouldBeTrue();
        (await Collect(client.DownloadUpdateAsync("0.2.9", Ct)))[^1].Error.ShouldNotBeNull("другую версию служба не качает");
        (await Collect(client.DownloadUpdateAsync("0.3.0", Ct)))[^1].Ready.ShouldBeTrue();
        (await client.InstallUpdateAsync("0.3.0", Ct)).ShouldBe(4242);
    }

    [Fact]
    public void Version_history_remembers_the_older_previous_and_the_newer_one_left()
    {
        VersionHistory.Record(_state, "0.1.0").ShouldBe(new VersionHistory(null, null), "первый запуск — истории нет");
        VersionHistory.Record(_state, "0.1.0").ShouldBe(new VersionHistory(null, null));
        VersionHistory.Record(_state, "0.2.0").ShouldBe(new VersionHistory("0.1.0", null), "обновили — прежнюю можно вернуть");
        VersionHistory.Record(_state, "0.2.0").ShouldBe(new VersionHistory("0.1.0", null), "та же версия — история та же");
        VersionHistory.Record(_state, "0.1.0").ShouldBe(new VersionHistory(null, "0.2.0"), "откатились: 0.2.0 больше не предлагать, откатываться не на что");
        VersionHistory.Record(_state, "0.3.0").ShouldBe(new VersionHistory("0.1.0", null), "новое обновление — снова можно вернуться");
        File.WriteAllText(Path.Combine(_state, VersionHistory.CurrentFile), "мусор");
        VersionHistory.Record(_state, "0.4.0").Previous.ShouldBe("0.1.0", "испорченная запись — история не меняется");
        File.ReadAllText(Path.Combine(_state, VersionHistory.CurrentFile)).ShouldBe("0.4.0");
    }

    [Fact]
    public async Task Previous_version_is_offered_for_rollback_only_with_a_signed_release_for_this_install()
    {
        var deb = RandomNumberGenerator.GetBytes(1000);
        foreach (var version in new[] { "0.1.0", "0.2.0", "0.3.0" })
        {
            var manifest = ManifestWith(version, "linux-x64-deb", $"private-gateway_{version}_amd64.deb", deb);
            _site.Release(version, manifest, Sign(manifest));
        }

        using var checker = Checker("0.2.0", new VersionHistory("0.1.0", null), "linux-x64-deb");
        var offer = await checker.CheckAsync(UpdateChannel.Stable, force: false, Ct);

        (offer.Version, offer.Rollback, offer.ReturnTo).ShouldBe(("0.3.0", "0.1.0", "0.2.0"), "можно откатиться на 0.1.0, а после обновления — на 0.2.0");
        checker.Rollback!.Assets.ShouldContainKey("linux-x64-deb");
        _site.Lists.ShouldBe(1, "список выпусков — один раз на проверку");

        using var msi = Checker("0.2.0", new VersionHistory("0.1.0", null), "win-x64-msi");
        var other = await msi.CheckAsync(UpdateChannel.Stable, force: false, Ct);
        (other.Rollback, other.ReturnTo).ShouldBe((null, null), "в выпусках нет файла этой установки — обещать нечего");
        _journal.Tail(5).ShouldContain(e => e.Message == "Откатиться на 0.1.0 нельзя: в выпуске нет файла для этой установки");

        using var archive = Checker("0.2.0", new VersionHistory("0.1.0", null), asset: null);
        (await archive.CheckAsync(UpdateChannel.Stable, force: false, Ct)).Rollback.ShouldBeNull("служба не ставит — и не откатывает");

        using var unsigned = Checker("0.2.0", new VersionHistory("0.0.9", null), "linux-x64-deb");
        (await unsigned.CheckAsync(UpdateChannel.Stable, force: false, Ct)).Rollback.ShouldBeNull();
        _journal.Tail(5).ShouldContain(e => e.Message == "Откатиться на 0.0.9 нельзя: выпуска нет на GitHub");

        using var newer = Checker("0.2.0", new VersionHistory("0.3.0", null), "linux-x64-deb");
        (await newer.CheckAsync(UpdateChannel.Stable, force: false, Ct)).Rollback.ShouldBeNull("«прежняя» новее нынешней — это не откат");

        using var left = Checker("0.2.0", new VersionHistory(null, "0.3.0"), "linux-x64-deb");
        var after = await left.CheckAsync(UpdateChannel.Stable, force: false, Ct);
        (after.State, after.Rollback, after.ReturnTo).ShouldBe((UpdateState.UpToDate, null, null), "с 0.3.0 откатились — она не предлагается");
    }

    [Fact]
    public async Task Window_rolls_back_through_the_same_download_and_install()
    {
        var package = RandomNumberGenerator.GetBytes(40_000);
        var old = ManifestWith("0.1.0", "linux-x64-deb", "private-gateway_0.1.0_amd64.deb", package);
        _site.Release("0.1.0", old, Sign(old));
        _site.Asset("0.1.0", "private-gateway_0.1.0_amd64.deb", package);
        var launched = new List<string?>();
        await using var service = await RunningService.StartAsync(
            updates: journal => new UpdateChecker(Feed(), [_key.ExportSubjectPublicKeyInfoPem()], "0.2.0", 0, _time, journal, new VersionHistory("0.1.0", null), "linux-x64-deb"),
            installer: journal => new UpdateInstaller(_http, new Uri(_site.Url, "download/"), Path.Combine(_state, "updates"),
                new UpdateKind("linux-x64-deb", (file, _) =>
                {
                    launched.Add(Path.GetFileName(file));
                    return 4242;
                }), journal));
        using var client = new ServiceGatewayClient(IpcChannel.Create(service.Endpoint, system: false), ServiceKind.User, _ => null, () => AppSettings.Default, TimeProvider.System);

        (await Collect(client.DownloadUpdateAsync("0.1.0", Ct)))[^1].Error.ShouldNotBeNull("служба еще не проверяла — откатывать не на что");
        var offer = await client.CheckForUpdateAsync(UpdateChannel.Stable, force: false, Ct);
        (offer!.State, offer.Rollback).ShouldBe((UpdateState.UpToDate, "0.1.0"));
        (await Collect(client.DownloadUpdateAsync("0.1.0", Ct)))[^1].Ready.ShouldBeTrue();
        (await client.InstallUpdateAsync("0.1.0", Ct)).ShouldBe(4242);
        launched.ShouldBe(["private-gateway_0.1.0_amd64.deb"]);
    }

    private UpdateInstaller Installer(UpdateKind? kind) => new(_http, new Uri(_site.Url, "download/"), Path.Combine(_state, "updates"), kind, _journal);

    private static async Task<List<UpdateProgress>> Collect(IAsyncEnumerable<UpdateProgress> progress)
    {
        var list = new List<UpdateProgress>();
        await foreach (var step in progress)
        {
            list.Add(step);
        }

        return list;
    }

    private static byte[] ManifestWith(string version, string kind, string file, byte[] content) => Encoding.UTF8.GetBytes($$"""
        {"version":"{{version}}","channel":"stable","rollout":100,"published":"2026-10-26T08:00:00Z",
         "notes":"https://github.com/rvburov/private-gateway-client/releases/tag/v{{version}}",
         "assets":{"{{kind}}":{"file":"{{file}}","sha256":"{{Convert.ToHexStringLower(SHA256.HashData(content))}}","size":{{content.Length}}} } }
        """);

    private UpdateChecker Checker(string current, VersionHistory? history = null, string? asset = null) =>
        new(Feed(), [_key.ExportSubjectPublicKeyInfoPem()], current, 0, _time, _journal, history, asset);

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
        private int _lists;

        public ReleaseSite()
        {
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public Uri Url => new($"http://{(IPEndPoint)_listener.LocalEndpoint}/");

        public HttpStatusCode ListStatus { get; set; } = HttpStatusCode.OK;

        public int Requests => Volatile.Read(ref _requests);

        public int Redirects => Volatile.Read(ref _redirects);

        /// <summary>Сколько раз спросили список выпусков.</summary>
        public int Lists => Volatile.Read(ref _lists);

        public void Release(string version, byte[] manifest, string signature, bool prerelease = false, bool chunked = false)
        {
            lock (_files)
            {
                _releases.Add((version, prerelease));
                _files[$"/storage/v{version}/update.json"] = (manifest, chunked);
                _files[$"/storage/v{version}/update.json.sig"] = (Encoding.ASCII.GetBytes(signature), false);
            }
        }

        /// <summary>Файл выпуска: /download/v…/файл → переадресация → /storage/v…/файл.</summary>
        public void Asset(string version, string file, byte[] content, bool chunked = false)
        {
            lock (_files)
            {
                _files[$"/storage/v{version}/{file}"] = (content, chunked);
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
                    Interlocked.Increment(ref _lists);
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
