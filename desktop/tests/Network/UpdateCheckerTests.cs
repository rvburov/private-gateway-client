using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
/// Обновления в службе по ленте сайта (<see cref="UpdateChecker"/>, <see cref="UpdateInstaller"/>): ленту окно передает
/// байтами (здесь ее собирает тест, подписывая манифесты своим ключом), файлы служба качает по билетам с «сайта» на
/// 127.0.0.1 — настоящий HTTP, как <c>/api/app/updates/files/&lt;билет&gt;</c>, — и вызовы окна через IPC.
/// </summary>
public sealed class UpdateCheckerTests : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero));
    private readonly ServiceJournal _journal;
    private readonly UpdateSite _site = new();
    private readonly HttpClient _http = UpdateInstaller.CreateHttp();
    private readonly string _state = Directory.CreateTempSubdirectory("pg-update-").FullName;

    public UpdateCheckerTests() => _journal = new ServiceJournal(_time);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Адрес файлов «сайта»: ссылки служба берет только отсюда.
    private Uri Files => new(_site.Url, "api/app/updates/files/");

    public void Dispose()
    {
        _key.Dispose();
        _site.Dispose();
        _http.Dispose();
        Directory.Delete(_state, recursive: true);
    }

    [Fact]
    public void Signed_release_in_the_feed_is_found_without_the_network()
    {
        var checker = Checker("0.2.0");
        var feed = Feed(Release("0.3.0", Manifest("0.3.0")));

        var offer = checker.Check(UpdateChannel.Stable, feed);

        offer.State.ShouldBe(UpdateState.Available);
        (offer.Version, offer.Checked).ShouldBe(("0.3.0", _time.GetUtcNow()));
        offer.Highlights.ShouldBe(["Новое"]);
        checker.Found!.Assets["win-x64-msi"].File.ShouldBe("PrivateGateway-0.3.0-win-x64.msi");
        _site.Requests.ShouldBe(0, "проверка — по ленте окна, служба в сеть не ходит");
        checker.Check(UpdateChannel.Beta, feed).Version.ShouldBe("0.3.0", "бета-канал видит и стабильные выпуски");
        _journal.Tail(10).Count(e => e.Message.Contains("Доступно обновление 0.3.0", StringComparison.Ordinal)).ShouldBe(1, "одна и та же версия в журнал — один раз");
    }

    [Fact]
    public void Unreadable_or_oversized_feed_is_a_failure_in_the_journal_and_is_not_remembered()
    {
        var checker = Checker("0.2.0");
        checker.Check(UpdateChannel.Stable, Feed(Release("0.3.0", Manifest("0.3.0")))).State.ShouldBe(UpdateState.Available);

        checker.Check(UpdateChannel.Stable, "<html>captive portal</html>"u8).State.ShouldBe(UpdateState.Failed);
        _journal.Tail(5).ShouldContain(e => e.Level == JournalLevel.Warning && e.Message.StartsWith("Проверить обновления не удалось", StringComparison.Ordinal));
        checker.Check(UpdateChannel.Stable, new byte[ReleaseEntry.MaxFeedBytes + 1]).State.ShouldBe(UpdateState.Failed);
        _journal.Tail(5).ShouldContain(e => e.Message.Contains("больше", StringComparison.Ordinal), "ленту больше 1 МБ служба не читает");
        checker.Found!.Version.ShouldBe("0.3.0", "неудача найденного прежде не стирает");

        checker.Check(UpdateChannel.Stable, Feed(Release("0.3.1", Manifest("0.3.1")))).Version.ShouldBe("0.3.1", "неудача не запоминается");
    }

    [Fact]
    public void Skipped_releases_are_named_in_the_journal()
    {
        var checker = Checker("0.2.0");

        checker.Check(UpdateChannel.Stable, Feed(Release("0.3.0", Manifest("0.3.0"), signature: Sign(Manifest("0.2.9"))))).State.ShouldBe(UpdateState.UpToDate);

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
    public void Files_of_updates_are_only_on_https_or_this_computer()
    {
        Should.Throw<ArgumentException>(() => new UpdateChecker([], "0.2.0", 0, _time, _journal, files: new Uri("http://example.com/api/app/updates/files/")));
        _ = new UpdateChecker([], "0.2.0", 0, _time, _journal, files: new Uri("https://example.com/api/app/updates/files/"));
    }

    [Fact]
    public async Task Window_asks_through_ipc_with_its_channel_and_the_site_feed()
    {
        var feed = Feed(Release("0.4.0-beta.1", Manifest("0.4.0-beta.1", "beta"), channel: "beta"), Release("0.3.0", Manifest("0.3.0")));
        await using var service = await RunningService.StartAsync(updates: journal => Checker("0.2.0", journal: journal));
        using var client = Client(service);

        var stable = await client.CheckForUpdateAsync(UpdateChannel.Stable, force: false, feed, Ct);
        stable!.Version.ShouldBe("0.3.0");
        stable.Installable.ShouldBeFalse("служба без установщика не ставит — окно ведет на сайт");
        stable.Notes.ShouldBe(new Uri("https://private-gateway.duckdns.org/downloads"));
        (await client.CheckForUpdateAsync(UpdateChannel.Beta, force: false, feed, Ct))!.Version.ShouldBe("0.4.0-beta.1");

        (await client.CheckForUpdateAsync(UpdateChannel.Stable, force: true, null, Ct))!.State.ShouldBe(UpdateState.NoFeed, "окно без ленты — службе проверять нечего");
        _site.Requests.ShouldBe(0, "и без ленты в сеть не ходит");
        service.Journal.Tail(5).ShouldContain(e => e.Message.StartsWith("Окно не передало ленту обновлений сайта", StringComparison.Ordinal));
        var refused = await Should.ThrowAsync<RpcException>(async () =>
            await service.Client.CheckForUpdateAsync(new V1.CheckForUpdateRequest(), cancellationToken: Ct));
        refused.StatusCode.ShouldBe(StatusCode.InvalidArgument, "канал не задан — служба не угадывает");
    }

    [Fact]
    public async Task Service_without_keys_says_so_and_does_not_read_the_feed()
    {
        await using var service = await RunningService.StartAsync();

        var offer = await service.Client.CheckForUpdateAsync(new V1.CheckForUpdateRequest
        {
            Channel = V1.UpdateChannel.Stable,
            SiteFeed = Google.Protobuf.ByteString.CopyFromUtf8("не лента"),
        }, cancellationToken: Ct);

        offer.State.ShouldBe(V1.UpdateState.NotSigned);
    }

    [Fact]
    public async Task Update_file_is_downloaded_by_its_ticket_with_progress_checked_against_the_manifest_and_handed_to_the_installer()
    {
        var package = RandomNumberGenerator.GetBytes(300_000);
        var manifest = UpdateManifest.Parse(ManifestWith("0.3.0", "linux-x64-deb", "private-gateway_0.3.0_amd64.deb", package));
        var ticket = _site.Ticket(package);
        var launched = new List<(string File, string Log)>();
        var installer = Installer(new UpdateKind("linux-x64-deb", (file, log) =>
        {
            launched.Add((file, log));
            return 4242;
        }));
        Directory.CreateDirectory(Path.Combine(_state, "updates"));
        File.WriteAllText(Path.Combine(_state, "updates", "private-gateway_0.2.0_amd64.deb"), "прежняя загрузка");

        var progress = await Collect(installer.DownloadAsync(manifest, ticket, Ct));

        progress[^1].ShouldBe(new UpdateProgress(package.Length, package.Length, Ready: true));
        progress.ShouldAllBe(p => p.Total == package.Length && p.Error == null);
        File.ReadAllBytes(Path.Combine(_state, "updates", "private-gateway_0.3.0_amd64.deb")).ShouldBe(package);
        Directory.GetFiles(Path.Combine(_state, "updates")).Select(Path.GetFileName).ShouldBe(["private-gateway_0.3.0_amd64.deb"], "прежние загрузки убраны");
        var requests = _site.Requests;
        (await Collect(installer.DownloadAsync(manifest, null, Ct))).ShouldBe([new UpdateProgress(package.Length, package.Length, Ready: true)],
            "уже скачанное и сверенное не качается заново — и ссылка не нужна");
        _site.Requests.ShouldBe(requests);

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
        var installer = Installer(new UpdateKind("linux-x64-deb", (_, _) => throw new InvalidOperationException("не должен запускаться")));

        var progress = await Collect(installer.DownloadAsync(manifest, _site.Ticket(tampered), Ct));

        progress[^1].Error!.ShouldContain("SHA-256");
        Directory.GetFiles(Path.Combine(_state, "updates")).ShouldBeEmpty("подмененный файл не остается ни целым, ни частью");
        Should.Throw<IOException>(() => installer.Install(manifest));

        (await Collect(installer.DownloadAsync(manifest, _site.Ticket([.. package, 1]), Ct)))[^1].Error!.ShouldContain("байт", customMessage: "длиннее манифеста — отказ");
        (await Collect(installer.DownloadAsync(manifest, _site.Ticket([.. package, 1], chunked: true), Ct)))[^1].Error!
            .ShouldContain("больше", customMessage: "без длины в заголовке — отказ на первом лишнем байте");
        (await Collect(installer.DownloadAsync(manifest, null, Ct)))[^1].Error!.ShouldContain("нет ссылки", customMessage: "не скачано и ссылки нет — отказ");

        (await Collect(installer.DownloadAsync(manifest, _site.Ticket(package), Ct)))[^1].Ready.ShouldBeTrue();
        var file = Path.Combine(_state, "updates", "private-gateway_0.3.0_amd64.deb");
        File.WriteAllBytes(file, tampered);
        Should.Throw<IOException>(() => installer.Install(manifest), "файл подменили после загрузки — перед установкой сверяется заново");
        (await Collect(Installer(null).DownloadAsync(manifest, _site.Ticket(package), Ct)))[^1].Error.ShouldBe("в выпуске нет файла для этой установки");
    }

    [Fact]
    public async Task Window_downloads_and_installs_only_what_the_service_found()
    {
        var package = RandomNumberGenerator.GetBytes(50_000);
        var manifest = ManifestWith("0.3.0", "linux-x64-deb", "private-gateway_0.3.0_amd64.deb", package);
        await using var service = await StartAsync("0.2.0", launch: (_, _) => 4242);
        using var client = Client(service);
        var feed = Feed(Release("0.3.0", manifest, files: [("linux-x64-deb", _site.Ticket(package))]));

        (await Collect(client.DownloadUpdateAsync("0.3.0", feed, Ct)))[^1].Error.ShouldNotBeNull("служба еще не находила 0.3.0 — качать нечего");
        (await client.InstallUpdateAsync("0.3.0", Ct)).ShouldBeNull();
        _site.Requests.ShouldBe(0);

        (await client.CheckForUpdateAsync(UpdateChannel.Stable, force: false, feed, Ct))!.Installable.ShouldBeTrue();
        (await Collect(client.DownloadUpdateAsync("0.2.9", feed, Ct)))[^1].Error.ShouldNotBeNull("другую версию служба не качает");
        (await Collect(client.DownloadUpdateAsync("0.3.0", feed, Ct)))[^1].Ready.ShouldBeTrue();
        (await client.InstallUpdateAsync("0.3.0", Ct)).ShouldBe(4242);
    }

    [Fact]
    public async Task Expired_ticket_is_a_failure_not_a_crash_and_a_fresh_feed_downloads()
    {
        // Билеты живут 30 минут: окно проверило, а скачать попросило позже со старой лентой — сайт отвечает 403.
        var package = RandomNumberGenerator.GetBytes(80_000);
        var manifest = ManifestWith("0.3.0", "linux-x64-deb", "private-gateway_0.3.0_amd64.deb", package);
        await using var service = await StartAsync("0.2.0", launch: (_, _) => 4242);
        using var client = Client(service);
        var old = Feed(Release("0.3.0", manifest, files: [("linux-x64-deb", _site.Ticket(package, expired: true))]));
        (await client.CheckForUpdateAsync(UpdateChannel.Stable, force: false, old, Ct))!.Version.ShouldBe("0.3.0");

        var failed = await Collect(client.DownloadUpdateAsync("0.3.0", old, Ct));

        failed[^1].Error.ShouldBe("сайт не отдал файл (403): ссылка на файл устарела — проверьте обновления еще раз");
        failed.ShouldNotContain(p => p.Ready);
        service.Journal.Tail(5).ShouldContain(e => e.Message.StartsWith("Обновление 0.3.0 не скачалось: сайт не отдал файл (403)", StringComparison.Ordinal));
        service.Journal.Tail(20).ShouldNotContain(e => e.Message.Contains("/api/app/updates/files/", StringComparison.Ordinal), "билет в журнал не попадает");
        Directory.GetFiles(Path.Combine(_state, "updates")).ShouldBeEmpty("от неудачной загрузки ничего не остается");
        (await client.InstallUpdateAsync("0.3.0", Ct)).ShouldBeNull("нескачанное не ставится");

        var fresh = Feed(Release("0.3.0", manifest, files: [("linux-x64-deb", _site.Ticket(package))]));
        (await Collect(client.DownloadUpdateAsync("0.3.0", fresh, Ct)))[^1].Ready.ShouldBeTrue("служба жива, свежий билет — файл скачан");
        (await client.InstallUpdateAsync("0.3.0", Ct)).ShouldBe(4242);
        _site.Expired.ShouldBe(1);
    }

    [Fact]
    public async Task Link_off_the_site_files_address_or_a_redirect_is_never_followed()
    {
        var package = RandomNumberGenerator.GetBytes(20_000);
        var manifest = ManifestWith("0.3.0", "linux-x64-deb", "private-gateway_0.3.0_amd64.deb", package);
        using var other = new UpdateSite();
        await using var service = await StartAsync("0.2.0", launch: (_, _) => 4242);
        using var client = Client(service);
        var elsewhere = Feed(Release("0.3.0", manifest, files: [("linux-x64-deb", other.Ticket(package))]));
        (await client.CheckForUpdateAsync(UpdateChannel.Stable, force: false, elsewhere, Ct))!.Installable
            .ShouldBeFalse("ссылка не на адрес файлов сайта не в счет — ставить не по чему");

        (await Collect(client.DownloadUpdateAsync("0.3.0", elsewhere, Ct)))[^1].Error!.ShouldContain("нет ссылки");
        other.Requests.ShouldBe(0, "ссылка не на адрес файлов сайта — служба туда не ходит");
        service.Journal.Tail(5).ShouldContain(e => e.Message == "Обновление 0.3.0 не скачать: в ленте сайта нет ссылки на файл linux-x64-deb");

        var redirect = Feed(Release("0.3.0", manifest, files: [("linux-x64-deb", _site.Redirect(other.Ticket(package)))]));
        (await Collect(client.DownloadUpdateAsync("0.3.0", redirect, Ct)))[^1].Error.ShouldBe("сайт не отдал файл (302): переадресация — файл берется только по ссылке сайта");
        other.Requests.ShouldBe(0, "переадресацию с билета служба не выполняет");
        (await Collect(client.DownloadUpdateAsync("0.3.0", null, Ct)))[^1].Error!.ShouldContain("нет ссылки", customMessage: "без ленты — не по чему");
    }

    [Theory]
    [InlineData("ticket_expired", "сайт не отдал файл (403): ссылка на файл устарела — проверьте обновления еще раз")]
    [InlineData("invalid_ticket", "сайт не отдал файл (403): сайт не принял ссылку на файл")]
    [InlineData("forbidden", "сайт не отдал файл (403): сайт закрыл этому устройству доступ")]
    [InlineData("что-то новое", "сайт не отдал файл (403): ошибка сайта")]
    [InlineData(null, "сайт не отдал файл (404): файла нет на сайте")]
    public async Task Refused_ticket_is_a_clean_failure_with_the_reason(string? code, string error)
    {
        var package = RandomNumberGenerator.GetBytes(10_000);
        var manifest = UpdateManifest.Parse(ManifestWith("0.3.0", "linux-x64-deb", "private-gateway_0.3.0_amd64.deb", package));
        var installer = Installer(new UpdateKind("linux-x64-deb", (_, _) => throw new InvalidOperationException("не должен запускаться")));
        var url = code is null ? new Uri(Files, "t-unknown") : _site.Refused(code);

        var progress = await Collect(installer.DownloadAsync(manifest, url, Ct));

        progress[^1].Error.ShouldBe(error);
        progress.ShouldNotContain(p => p.Ready);
        Directory.GetFiles(Path.Combine(_state, "updates")).ShouldBeEmpty();
        (await Collect(installer.DownloadAsync(manifest, _site.Ticket(package), Ct)))[^1].Ready.ShouldBeTrue("отказ не ломает следующую загрузку");
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
    public void Previous_version_is_offered_for_rollback_only_with_a_signed_release_for_this_install()
    {
        var deb = RandomNumberGenerator.GetBytes(1000);
        string Deb(string version, bool onSite = true) => Release(version, ManifestWith(version, "linux-x64-deb", $"private-gateway_{version}_amd64.deb", deb),
            files: onSite ? [("linux-x64-deb", _site.Ticket(deb))] : []);
        var feed = Feed(Deb("0.3.0"), Deb("0.2.0"), Deb("0.1.0"));

        var checker = Checker("0.2.0", new VersionHistory("0.1.0", null), "linux-x64-deb");
        var offer = checker.Check(UpdateChannel.Stable, feed);

        (offer.Version, offer.Installable, offer.Rollback, offer.ReturnTo).ShouldBe(("0.3.0", true, "0.1.0", "0.2.0"),
            "можно откатиться на 0.1.0, а после обновления — на 0.2.0");
        checker.Rollback!.Assets.ShouldContainKey("linux-x64-deb");

        // Сайт отдает выпуск и без файлов ОС (пустой files): подписан — но ставить и откатывать не по чему.
        var notOnSite = Checker("0.2.0", new VersionHistory("0.1.0", null), "linux-x64-deb").Check(UpdateChannel.Stable,
            Feed(Deb("0.3.0", onSite: false), Deb("0.2.0", onSite: false), Deb("0.1.0", onSite: false)));
        (notOnSite.State, notOnSite.Version, notOnSite.Installable, notOnSite.Rollback, notOnSite.ReturnTo)
            .ShouldBe((UpdateState.Available, "0.3.0", false, null, null), "выпуск есть, а файла на сайте нет — «скачать на сайте», не ошибка");
        _journal.Tail(5).ShouldContain(e => e.Message == "Откатиться на 0.1.0 нельзя: файла для этой установки нет на сайте");
        _journal.Tail(5).ShouldContain(e => e.Message == "Доступно обновление 0.3.0 (подпись проверена); файла linux-x64-deb на сайте нет — служба его не поставит");

        var other = Checker("0.2.0", new VersionHistory("0.1.0", null), "win-x64-msi").Check(UpdateChannel.Stable, feed);
        (other.Rollback, other.ReturnTo).ShouldBe((null, null), "в выпусках нет файла этой установки — обещать нечего");
        _journal.Tail(5).ShouldContain(e => e.Message == "Откатиться на 0.1.0 нельзя: в выпуске нет файла для этой установки");

        Checker("0.2.0", new VersionHistory("0.1.0", null), asset: null).Check(UpdateChannel.Stable, feed).Rollback.ShouldBeNull("служба не ставит — и не откатывает");

        Checker("0.2.0", new VersionHistory("0.0.9", null), "linux-x64-deb").Check(UpdateChannel.Stable, feed).Rollback.ShouldBeNull();
        _journal.Tail(5).ShouldContain(e => e.Message == "Откатиться на 0.0.9 нельзя: выпуска нет в ленте сайта");

        Checker("0.2.0", new VersionHistory("0.3.0", null), "linux-x64-deb").Check(UpdateChannel.Stable, feed).Rollback
            .ShouldBeNull("«прежняя» новее нынешней — это не откат");

        var after = Checker("0.2.0", new VersionHistory(null, "0.3.0"), "linux-x64-deb").Check(UpdateChannel.Stable, feed);
        (after.State, after.Rollback, after.ReturnTo).ShouldBe((UpdateState.UpToDate, null, null), "с 0.3.0 откатились — она не предлагается");
    }

    [Fact]
    public async Task Window_rolls_back_through_the_same_download_and_install()
    {
        var package = RandomNumberGenerator.GetBytes(40_000);
        var old = ManifestWith("0.1.0", "linux-x64-deb", "private-gateway_0.1.0_amd64.deb", package);
        var launched = new List<string?>();
        await using var service = await StartAsync("0.2.0", new VersionHistory("0.1.0", null), (file, _) =>
        {
            launched.Add(Path.GetFileName(file));
            return 4242;
        });
        using var client = Client(service);
        var feed = Feed(Release("0.1.0", old, files: [("linux-x64-deb", _site.Ticket(package))]));

        (await Collect(client.DownloadUpdateAsync("0.1.0", feed, Ct)))[^1].Error.ShouldNotBeNull("служба еще не проверяла — откатывать не на что");
        var offer = await client.CheckForUpdateAsync(UpdateChannel.Stable, force: false, feed, Ct);
        (offer!.State, offer.Rollback).ShouldBe((UpdateState.UpToDate, "0.1.0"));
        (await Collect(client.DownloadUpdateAsync("0.1.0", feed, Ct)))[^1].Ready.ShouldBeTrue();
        (await client.InstallUpdateAsync("0.1.0", Ct)).ShouldBe(4242);
        launched.ShouldBe(["private-gateway_0.1.0_amd64.deb"]);
    }

    private static ServiceGatewayClient Client(RunningService service) =>
        new(IpcChannel.Create(service.Endpoint, system: false), ServiceKind.User, _ => null, () => AppSettings.Default, TimeProvider.System);

    // Служба со своей проверкой (лента с ключом теста, файлы — на «сайте») и установщиком .deb.
    private Task<RunningService> StartAsync(string current, VersionHistory? history = null, Func<string, string, int>? launch = null) =>
        RunningService.StartAsync(
            updates: journal => Checker(current, history, "linux-x64-deb", journal),
            installer: journal => new UpdateInstaller(_http, Path.Combine(_state, "updates"),
                new UpdateKind("linux-x64-deb", launch ?? ((_, _) => throw new InvalidOperationException("установка не просилась"))), journal));

    private UpdateInstaller Installer(UpdateKind? kind) => new(_http, Path.Combine(_state, "updates"), kind, _journal);

    private UpdateChecker Checker(string current, VersionHistory? history = null, string? asset = null, ServiceJournal? journal = null) =>
        new([_key.ExportSubjectPublicKeyInfoPem()], current, 0, _time, journal ?? _journal, history, asset, Files);

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
         "notes":"https://private-gateway.duckdns.org/downloads",
         "assets":{"{{kind}}":{"file":"{{file}}","sha256":"{{Convert.ToHexStringLower(SHA256.HashData(content))}}","size":{{content.Length}}} } }
        """);

    private static byte[] Manifest(string version, string channel = "stable") => Encoding.UTF8.GetBytes($$"""
        {"version":"{{version}}","channel":"{{channel}}","rollout":100,"published":"2026-10-26T08:00:00Z",
         "notes":"https://private-gateway.duckdns.org/downloads","highlights":["Новое"],
         "assets":{"win-x64-msi":{"file":"PrivateGateway-{{version}}-win-x64.msi","sha256":"{{new string('a', 64)}}","size":10} } }
        """);

    private string Sign(byte[] manifest) =>
        Convert.ToBase64String(_key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) + "\n";

    /// <summary>Выпуск в ленте сайта: манифест base64 байт в байт, подпись (по умолчанию — ключом теста) и ссылки на файлы.</summary>
    private string Release(string version, byte[] manifest, string? signature = null, string channel = "stable", (string Kind, Uri Url)[]? files = null) =>
        JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["version"] = version,
            ["channel"] = channel,
            ["manifest"] = Convert.ToBase64String(manifest),
            ["signature"] = signature ?? Sign(manifest),
            ["files"] = (files ?? []).ToDictionary(f => f.Kind, f => new Dictionary<string, string> { ["url"] = f.Url.AbsoluteUri, ["expires_at"] = "2026-10-26T09:30:00Z" }),
        });

    private static byte[] Feed(params string[] releases) => Encoding.UTF8.GetBytes($$"""{"releases":[{{string.Join(',', releases)}}]}""");

    /// <summary>
    /// «Сайт» на 127.0.0.1: /api/app/updates/files/&lt;билет&gt; — файл по билету, истекший билет — 403, билет-переадресация —
    /// 302; остальное — 404. chunked — без длины в заголовках.
    /// </summary>
    private sealed class UpdateSite : IDisposable
    {
        private const string FilesPath = "/api/app/updates/files/";
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Dictionary<string, (byte[]? Body, bool Chunked, bool Expired, Uri? Location, string? Code)> _tickets = new(StringComparer.Ordinal);
        private int _requests;
        private int _expired;

        public UpdateSite()
        {
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public Uri Url => new($"http://{(IPEndPoint)_listener.LocalEndpoint}/");

        public int Requests => Volatile.Read(ref _requests);

        /// <summary>Сколько раз пришли с истекшим билетом.</summary>
        public int Expired => Volatile.Read(ref _expired);

        /// <summary>Билет на файл: ссылка, как в ленте сайта.</summary>
        public Uri Ticket(byte[] content, bool chunked = false, bool expired = false) => Add((content, chunked, expired, null, null));

        /// <summary>Билет, с которого сайт переадресует на <paramref name="location"/>.</summary>
        public Uri Redirect(Uri location) => Add((null, false, false, location, null));

        /// <summary>Билет, на который сайт отвечает 403 с кодом <paramref name="code"/>.</summary>
        public Uri Refused(string code) => Add((null, false, false, null, code));

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }

        private Uri Add((byte[]? Body, bool Chunked, bool Expired, Uri? Location, string? Code) ticket)
        {
            var name = "t" + Guid.NewGuid().ToString("N") + ".sig";
            lock (_tickets)
            {
                _tickets[name] = ticket;
            }

            return new Uri(Url, FilesPath.TrimStart('/') + name);
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
            lock (_tickets)
            {
                if (!path.StartsWith(FilesPath, StringComparison.Ordinal) || !_tickets.TryGetValue(path[FilesPath.Length..], out var ticket))
                {
                    return Response("404 Not Found", []);
                }

                if (ticket.Expired)
                {
                    Interlocked.Increment(ref _expired);
                    return Response("403 Forbidden", "{\"error\":{\"code\":\"ticket_expired\"}}"u8.ToArray());
                }

                if (ticket.Code is { } code)
                {
                    return Response("403 Forbidden", Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = new { code } })));
                }

                return ticket.Location is { } location
                    ? Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: {location.AbsoluteUri}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
                    : Response("200 OK", ticket.Body!, ticket.Chunked);
            }
        }

        private static byte[] Response(string status, byte[] body, bool chunked = false) => chunked
            ? [.. Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n{body.Length:x}\r\n"), .. body, .. "\r\n0\r\n\r\n"u8]
            : [.. Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n"), .. body];
    }
}
