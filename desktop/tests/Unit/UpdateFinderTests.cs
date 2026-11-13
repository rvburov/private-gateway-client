using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core.Account;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Core.Updates;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// Обновление программы: манифест выпуска (<see cref="UpdateManifest"/>), его подпись (<see cref="UpdateSignature"/>),
/// лента обновлений сайта (<see cref="ReleaseEntry.ParseSite"/>) и выбор выпуска (<see cref="UpdateFinder"/>) — канал,
/// версия, подпись, доля раскатки. Ключи — созданные здесь же, как их создает scripts/release.cs keygen.
/// </summary>
public sealed class UpdateFinderTests : IDisposable
{
    private const string Ticket = "https://private-gateway.duckdns.org/api/app/updates/files/";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public void Dispose()
    {
        _key.Dispose();
        _otherKey.Dispose();
    }

    private string PublicKey => _key.ExportSubjectPublicKeyInfoPem();

    [Fact]
    public void Manifest_is_read_with_its_assets_and_highlights()
    {
        var manifest = UpdateManifest.Parse(Manifest("0.3.0", rollout: 20));

        manifest.Version.ShouldBe("0.3.0");
        manifest.Channel.ShouldBe(UpdateChannel.Stable);
        manifest.Rollout.ShouldBe(20);
        manifest.Notes.ShouldBe(AccountSite.DownloadsPage);
        manifest.Highlights.ShouldBe(["Быстрее переподключение после сна"]);
        manifest.Assets["win-x64-msi"].ShouldBe(new UpdateAsset("PrivateGateway-0.3.0-win-x64.msi", new string('a', 64), 1234));
        manifest.Assets["linux-x64-deb"].File.ShouldBe("private-gateway_0.3.0_amd64.deb");
    }

    [Theory]
    [InlineData("\"version\": \"0.3.0\"", "\"version\": \"v0.3.0\"")]
    [InlineData("\"channel\": \"stable\"", "\"channel\": \"nightly\"")]
    [InlineData("\"rollout\": 20", "\"rollout\": -1")]
    [InlineData("\"rollout\": 20", "\"rollout\": 101")]
    [InlineData("https://private-gateway.duckdns.org", "http://private-gateway.duckdns.org")]
    [InlineData("PrivateGateway-0.3.0-win-x64.msi", "../PrivateGateway-0.3.0-win-x64.msi")]
    [InlineData("PrivateGateway-0.3.0-win-x64.msi", "C:\\\\Windows\\\\evil.msi")]
    [InlineData("\"size\": 1234", "\"size\": 0")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("\"published\"", "\"released\"")]
    public void Anything_wrong_in_the_manifest_is_a_refusal_not_a_guess(string from, string to)
    {
        var json = Encoding.UTF8.GetString(Manifest("0.3.0", rollout: 20));
        json.ShouldContain(from);

        Should.Throw<FormatException>(() => UpdateManifest.Parse(Encoding.UTF8.GetBytes(json.Replace(from, to, StringComparison.Ordinal))));
    }

    [Fact]
    public void Signature_holds_only_for_these_bytes_and_a_program_key()
    {
        var manifest = Manifest("0.3.0");
        var signature = Sign(manifest);

        UpdateSignature.Verify(manifest, signature, [PublicKey]).ShouldBeTrue();
        UpdateSignature.Verify(manifest, signature, [_otherKey.ExportSubjectPublicKeyInfoPem(), PublicKey]).ShouldBeTrue("подходит любой ключ программы — так ключ меняется");
        UpdateSignature.Verify(manifest, signature, [_otherKey.ExportSubjectPublicKeyInfoPem()]).ShouldBeFalse("чужой ключ");
        UpdateSignature.Verify(manifest, signature, []).ShouldBeFalse();
        var changed = manifest.ToArray();
        changed[^3] ^= 1;
        UpdateSignature.Verify(changed, signature, [PublicKey]).ShouldBeFalse("один бит манифеста");
        UpdateSignature.Verify(manifest, "не base64", [PublicKey]).ShouldBeFalse();
        UpdateSignature.Verify(manifest, signature, ["-----BEGIN PUBLIC KEY-----\nmusor\n-----END PUBLIC KEY-----"]).ShouldBeFalse();

        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        UpdateSignature.Verify(manifest, Convert.ToBase64String(p384.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)),
            [p384.ExportSubjectPublicKeyInfoPem()]).ShouldBeFalse("ключ программы — только P-256");
    }

    [Fact]
    public void Site_feed_brings_the_manifest_byte_for_byte_and_a_rewritten_one_does_not_hold()
    {
        // Сайт хранит update.json как есть: служба проверяет подпись над этими байтами, а не над разобранным JSON.
        var manifest = Manifest("0.3.0");
        var signature = Sign(manifest);

        var release = ReleaseEntry.ParseSite(SiteFeed(Release("0.3.0", manifest, signature + "\n", files: """{"win-x64-msi":{"url":"https://private-gateway.duckdns.org/api/app/updates/files/t1.abc","expires_at":"2026-11-13T10:30:00Z"}}""")),
            AccountSite.UpdateFiles).ShouldHaveSingleItem();

        release.Manifest.ShouldBe(manifest);
        (release.Version, release.Channel, release.Signature).ShouldBe(("0.3.0", UpdateChannel.Stable, signature + "\n"));
        release.Files.ShouldBe(new Dictionary<string, Uri> { ["win-x64-msi"] = new(Ticket + "t1.abc") });
        UpdateFinder.Find([release], [PublicKey], "0.2.0", UpdateChannel.Stable, bucket: 0).Manifest!.Version.ShouldBe("0.3.0");

        // Тот же манифест, пересобранный без пробелов, — уже другие байты: подпись прежняя не подходит.
        var rewritten = Encoding.UTF8.GetBytes(System.Text.Json.Nodes.JsonNode.Parse(manifest)!.ToJsonString());
        rewritten.ShouldNotBe(manifest);
        var tampered = ReleaseEntry.ParseSite(SiteFeed(Release("0.3.0", rewritten, signature)), AccountSite.UpdateFiles);
        var search = UpdateFinder.Find(tampered, [PublicKey], "0.2.0", UpdateChannel.Stable, bucket: 0);
        search.State.ShouldBe(UpdateState.UpToDate);
        search.Skipped.ShouldBe(["0.3.0: подпись манифеста не сходится с ключом программы"]);
    }

    [Theory]
    [InlineData("https://private-gateway.duckdns.org/api/app/updates/files/t1.abc", true)]
    [InlineData("https://PRIVATE-GATEWAY.duckdns.org/api/app/updates/files/t1_-~%7C=", true)]
    [InlineData("http://private-gateway.duckdns.org/api/app/updates/files/t1", false)]
    [InlineData("https://private-gateway.duckdns.org:8443/api/app/updates/files/t1", false)]
    [InlineData("https://evil@private-gateway.duckdns.org/api/app/updates/files/t1", false)]
    [InlineData("https://private-gateway.duckdns.org.example.com/api/app/updates/files/t1", false)]
    [InlineData("https://example.com/api/app/updates/files/t1", false)]
    [InlineData("https://private-gateway.duckdns.org/api/app/updates/files/", false)]
    [InlineData("https://private-gateway.duckdns.org/api/app/updates/files/../../admin", false)]
    [InlineData("https://private-gateway.duckdns.org/api/app/updates/files/a/b", false)]
    [InlineData("https://private-gateway.duckdns.org/api/app/updates/files/t1?to=https://example.com", false)]
    [InlineData("https://private-gateway.duckdns.org/api/app/updates/files/t1#x", false)]
    [InlineData("https://private-gateway.duckdns.org/downloads/t1", false)]
    [InlineData("https://private-gateway.duckdns.org/api/app/updates/filesx/t1", false)]
    [InlineData("/api/app/updates/files/t1", false)]
    [InlineData("file:///etc/passwd", false)]
    public void Only_a_ticket_on_the_site_files_address_is_taken(string url, bool taken)
    {
        var release = ReleaseEntry.ParseSite(SiteFeed(Release("0.3.0", Manifest("0.3.0"), "c2ln", files: $$$"""{"linux-x64-deb":{"url":"{{{url}}}"}}""")),
            AccountSite.UpdateFiles).ShouldHaveSingleItem();

        release.Files.ContainsKey("linux-x64-deb").ShouldBe(taken, url);
    }

    [Fact]
    public void Odd_releases_are_skipped_and_what_is_not_a_feed_is_refused()
    {
        var manifest = Manifest("0.3.0");
        var releases = ReleaseEntry.ParseSite(SiteFeed(
            Release("v0.4.0", manifest, Sign(manifest)),
            Release("0.3.2", manifest, Sign(manifest), channel: "nightly"),
            """{"version":"0.3.1","channel":"stable","manifest":"не base64!","signature":"c2ln"}""",
            Release("0.3.0", manifest, new string('A', ReleaseEntry.MaxSignature + 1)),
            """{"version":"0.2.9","channel":"beta"}""",
            "17"), AccountSite.UpdateFiles);

        releases.Select(r => r.Version).ShouldBe(["0.3.1", "0.3.0", "0.2.9"], "версия не SemVer и незнакомый канал — не выпуски");
        releases[0].Manifest.ShouldBeNull("не base64 — манифеста нет");
        releases[1].Signature.ShouldBeNull("подпись длиннее предела — не она");
        (releases[2].Channel, releases[2].Manifest, releases[2].Signature, releases[2].Files.Count).ShouldBe((UpdateChannel.Beta, null, null, 0));
        UpdateFinder.Find(releases, [PublicKey], "0.2.0", UpdateChannel.Stable, bucket: 0).Skipped
            .ShouldBe(["0.3.1: выпуск без подписанного манифеста", "0.3.0: выпуск без подписанного манифеста"]);

        foreach (var json in new[] { """{"message":"Not Found"}""", "[]", "<html>captive portal</html>", """{"releases":{}}""", "" })
        {
            Should.Throw<FormatException>(() => ReleaseEntry.ParseSite(Encoding.UTF8.GetBytes(json), AccountSite.UpdateFiles), json);
        }
    }

    [Fact]
    public void Feed_over_a_megabyte_is_refused_before_it_is_read()
    {
        var empty = "{\"releases\":[],\"pad\":\"\"}";
        var full = Encoding.UTF8.GetBytes(empty.Replace("\"\"}", "\"" + new string('x', ReleaseEntry.MaxFeedBytes - empty.Length) + "\"}", StringComparison.Ordinal));
        full.Length.ShouldBe(ReleaseEntry.MaxFeedBytes);

        ReleaseEntry.ParseSite(full, AccountSite.UpdateFiles).ShouldBeEmpty("ровно мегабайт — еще лента");
        Should.Throw<FormatException>(() => ReleaseEntry.ParseSite([.. full, (byte)' '], AccountSite.UpdateFiles)).Message.ShouldContain("больше");
    }

    [Fact]
    public void Newer_signed_release_of_the_channel_is_found()
    {
        var newer = Entry("0.3.0", Manifest("0.3.0"));
        var search = UpdateFinder.Find([newer, Entry("0.2.0", Manifest("0.2.0"))], [PublicKey], "0.2.0", UpdateChannel.Stable, bucket: 50);

        search.State.ShouldBe(UpdateState.Available);
        search.Manifest!.Version.ShouldBe("0.3.0");
        search.Release.ShouldBeSameAs(newer, "и выпуск ленты — его ссылки на файлы");
        search.Skipped.ShouldBeEmpty();
    }

    [Fact]
    public void Same_or_older_version_is_up_to_date_and_is_not_even_checked()
    {
        var search = UpdateFinder.Find([Entry("0.2.0", Manifest("0.2.0"), signed: false), Entry("0.1.0", Manifest("0.1.0"), signed: false)], [PublicKey],
            "0.2.0", UpdateChannel.Stable, bucket: 0);

        search.State.ShouldBe(UpdateState.UpToDate);
        search.Skipped.ShouldBeEmpty("не новее установленной — подпись даже не проверяется");
    }

    [Fact]
    public void Without_program_keys_nothing_is_checked()
    {
        var search = UpdateFinder.Find([Entry("0.3.0", Manifest("0.3.0"))], [], "0.2.0", UpdateChannel.Stable, bucket: 0);

        (search.State, search.Skipped.Count).ShouldBe((UpdateState.NotSigned, 0));
    }

    [Fact]
    public void Stable_channel_skips_betas_and_beta_takes_them()
    {
        List<ReleaseEntry> releases = [Entry("0.4.0-beta.1", Manifest("0.4.0-beta.1", channel: "beta"), channel: "beta"), Entry("0.3.0", Manifest("0.3.0"))];
        var unsignedBeta = Entry("0.4.0-beta.2", Manifest("0.4.0-beta.2", channel: "beta"), signed: false, channel: "beta");

        var stable = UpdateFinder.Find([unsignedBeta, .. releases], [PublicKey], "0.2.0", UpdateChannel.Stable, bucket: 0);
        stable.Manifest!.Version.ShouldBe("0.3.0");
        stable.Skipped.ShouldBeEmpty("бету стабильный канал даже не проверяет");
        UpdateFinder.Find(releases, [PublicKey], "0.2.0", UpdateChannel.Beta, bucket: 0).Manifest!.Version.ShouldBe("0.4.0-beta.1");
    }

    [Fact]
    public void Rollout_share_decides_by_the_install_number_and_an_older_release_may_be_taken()
    {
        List<ReleaseEntry> releases = [Entry("0.4.0", Manifest("0.4.0", rollout: 20)), Entry("0.3.0", Manifest("0.3.0", rollout: 100))];

        UpdateFinder.Find(releases, [PublicKey], "0.2.0", UpdateChannel.Stable, bucket: 19).Manifest!.Version.ShouldBe("0.4.0", "номер 19 < 20 %");
        var later = UpdateFinder.Find(releases, [PublicKey], "0.2.0", UpdateChannel.Stable, bucket: 20);
        later.Manifest!.Version.ShouldBe("0.3.0", "до 0.4.0 раскатка еще не дошла, а 0.3.0 — у всех");
        later.Skipped.ShouldContain(s => s.Contains("раскатка 20 %", StringComparison.Ordinal));
        UpdateFinder.Find(releases, [PublicKey], "0.3.0", UpdateChannel.Stable, bucket: 99).State.ShouldBe(UpdateState.UpToDate);
    }

    [Fact]
    public void Stopped_rollout_offers_the_release_to_nobody_and_the_previous_one_stays()
    {
        List<ReleaseEntry> releases = [Entry("0.4.0", Manifest("0.4.0", rollout: 0)), Entry("0.3.0", Manifest("0.3.0", rollout: 100))];

        var search = UpdateFinder.Find(releases, [PublicKey], "0.2.0", UpdateChannel.Stable, bucket: 0);

        search.Manifest!.Version.ShouldBe("0.3.0", "остановленный выпуск не получает и установка с номером 0");
        search.Skipped.ShouldBe(["0.4.0: раскатка остановлена"]);
        UpdateFinder.Find(releases, [PublicKey], "0.3.0", UpdateChannel.Stable, bucket: 0).State.ShouldBe(UpdateState.UpToDate);
    }

    [Fact]
    public void Unsigned_foreign_signed_or_mismatched_releases_are_skipped_with_a_reason()
    {
        var search = UpdateFinder.Find(
            [
                Entry("0.6.0", Manifest("0.6.0"), signed: false),
                Entry("0.5.0", Manifest("0.5.0"), key: _otherKey),
                Entry("0.4.0", Manifest("0.3.9")),
                Entry("0.3.1", Manifest("0.3.1", channel: "beta")),
                Entry("0.3.0", Manifest("0.3.0")),
            ], [PublicKey], "0.2.0", UpdateChannel.Stable, bucket: 0);

        search.Manifest!.Version.ShouldBe("0.3.0");
        search.Skipped.Count.ShouldBe(4);
        search.Skipped[0].ShouldContain("без подписанного манифеста");
        search.Skipped[1].ShouldContain("подпись манифеста не сходится");
        search.Skipped[2].ShouldContain("манифест от другого выпуска", customMessage: "подписанный манифест 0.3.9 под версией 0.4.0 — подмена сайтом");
        search.Skipped[3].ShouldContain("манифест от другого выпуска", customMessage: "бета-манифест, который сайт назвал стабильным");
    }

    [Fact]
    public void Oversized_manifest_is_skipped_without_checking_its_signature()
    {
        var huge = new byte[UpdateManifest.MaxBytes + 1];

        var search = UpdateFinder.Find([Entry("0.4.0", huge), Entry("0.3.0", Manifest("0.3.0"))], [PublicKey], "0.2.0", UpdateChannel.Stable, bucket: 0);

        search.Manifest!.Version.ShouldBe("0.3.0");
        search.Skipped.ShouldBe(["0.4.0: манифест больше 64 КБ"]);
    }

    [Fact]
    public void Version_rolled_back_from_is_not_offered_again_but_a_newer_one_is()
    {
        List<ReleaseEntry> releases = [Entry("0.4.0", Manifest("0.4.0")), Entry("0.3.0", Manifest("0.3.0"))];

        var search = UpdateFinder.Find(releases, [PublicKey], "0.2.0", UpdateChannel.Stable, bucket: 0, skip: "0.4.0");

        search.Manifest!.Version.ShouldBe("0.3.0");
        search.Skipped.ShouldHaveSingleItem().ShouldContain("откатились");
        UpdateFinder.Find(releases, [PublicKey], "0.2.0", UpdateChannel.Stable, bucket: 0, skip: "0.3.0")
            .Manifest!.Version.ShouldBe("0.4.0", "вышла новее той, с которой откатились, — предлагается");
    }

    [Fact]
    public void Release_of_an_exact_version_is_taken_only_signed_and_true_to_its_version()
    {
        List<ReleaseEntry> releases =
        [
            Entry("0.3.0", Manifest("0.3.0")),
            Entry("0.2.0", Manifest("0.2.0"), signed: false),
            Entry("0.1.1", Manifest("0.1.0")),
            Entry("0.1.0", Manifest("0.1.0"), channel: "beta"),
        ];

        UpdateFinder.Release(releases, [PublicKey], "0.1.0").Manifest!.Version.ShouldBe("0.1.0", "канал не важен — эту версию ставили здесь");
        UpdateFinder.Release(releases, [PublicKey], "0.2.0").Problem.ShouldBe("выпуск без подписанного манифеста");
        UpdateFinder.Release(releases, [PublicKey], "0.1.1").Problem!.ShouldContain("манифест от другого выпуска");
        UpdateFinder.Release(releases, [PublicKey], "0.0.9").Problem.ShouldBe("выпуска нет в ленте сайта");
        UpdateFinder.Release(releases, [], "0.1.0").Manifest.ShouldBeNull();
        UpdateFinder.Release([Entry("0.1.0", Manifest("0.1.0"), key: _otherKey)], [PublicKey], "0.1.0").Problem
            .ShouldBe("подпись манифеста не сходится с ключом программы");
    }

    [Fact]
    public void Update_offer_goes_through_the_contract_and_back()
    {
        var at = new DateTimeOffset(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
        var offer = UpdateOffer.Found(UpdateManifest.Parse(Manifest("0.3.0")), at);

        var back = offer.ToContract().ToCore();

        back.State.ShouldBe(UpdateState.Available);
        back.Version.ShouldBe("0.3.0");
        back.Highlights.ShouldBe(["Быстрее переподключение после сна"]);
        back.Notes.ShouldBe(offer.Notes);
        back.Checked.ShouldBe(at);
        (back.Rollback, back.ReturnTo).ShouldBe((null, null));
        var both = (offer with { Rollback = "0.1.0", ReturnTo = "0.2.0" }).ToContract().ToCore();
        (both.Rollback, both.ReturnTo).ShouldBe(("0.1.0", "0.2.0"));
        new UpdateOffer(UpdateState.UpToDate, at).ToContract().ToCore().Version.ShouldBeNull();
        new UpdateOffer(UpdateState.NoFeed, at).ToContract().State.ShouldBe(V1.UpdateState.NoFeed);
        Should.Throw<ArgumentException>(() => new V1.UpdateOffer { State = V1.UpdateState.Available, Version = "" }.ToCore());
        foreach (var channel in Enum.GetValues<UpdateChannel>())
        {
            channel.ToContract().ToCore().ShouldBe(channel);
        }

        foreach (var state in Enum.GetValues<UpdateState>())
        {
            state.ToContract().ToCore().ShouldBe(state);
        }

        foreach (var state in Enum.GetValues<V1.UpdateState>().Where(s => s != V1.UpdateState.Unspecified))
        {
            state.ToCore().ToContract().ShouldBe(state);
        }

        Should.Throw<ArgumentOutOfRangeException>(() => V1.UpdateChannel.Unspecified.ToCore());
    }

    [Fact]
    public void Site_feed_travels_in_new_fields_and_requests_of_an_older_window_stay_readable()
    {
        // proto3: новые поля с новыми номерами — окно 0.2 и служба 0.2 их просто не знают.
        var check = V1.CheckForUpdateRequest.Descriptor.FindFieldByName("site_feed");
        (check.FieldNumber, check.FieldType).ShouldBe((3, FieldType.Bytes));
        var download = V1.DownloadUpdateRequest.Descriptor.FindFieldByName("site_feed");
        (download.FieldNumber, download.FieldType).ShouldBe((2, FieldType.Bytes));
        ((int)V1.UpdateState.NoFeed).ShouldBe(5, "новое значение — в конце, прежние номера не сдвигаются");

        var older = V1.CheckForUpdateRequest.Parser.ParseFrom(new V1.CheckForUpdateRequest { Channel = V1.UpdateChannel.Stable, Force = true }.ToByteArray());
        (older.Channel, older.Force, older.SiteFeed.IsEmpty).ShouldBe((V1.UpdateChannel.Stable, true, true), "окно без ленты — пустое поле");

        var feed = SiteFeed(Release("0.3.0", Manifest("0.3.0"), Sign(Manifest("0.3.0"))));
        V1.CheckForUpdateRequest.Parser.ParseFrom(new V1.CheckForUpdateRequest { Channel = V1.UpdateChannel.Beta, SiteFeed = ByteString.CopyFrom(feed) }.ToByteArray())
            .SiteFeed.ToByteArray().ShouldBe(feed, "лента доходит до службы байт в байт");
        V1.DownloadUpdateRequest.Parser.ParseFrom(new V1.DownloadUpdateRequest { Version = "0.3.0", SiteFeed = ByteString.CopyFrom(feed) }.ToByteArray())
            .SiteFeed.ToByteArray().ShouldBe(feed);
    }

    private static byte[] Manifest(string version, int rollout = 100, string channel = "stable") => Encoding.UTF8.GetBytes($$"""
        {
          "version": "{{version}}",
          "channel": "{{channel}}",
          "rollout": {{rollout}},
          "published": "2026-10-26T08:00:00Z",
          "notes": "https://private-gateway.duckdns.org/downloads",
          "highlights": [ "Быстрее переподключение после сна" ],
          "assets": {
            "win-x64-msi": { "file": "PrivateGateway-{{version}}-win-x64.msi", "sha256": "{{new string('a', 64)}}", "size": 1234 },
            "linux-x64-deb": { "file": "private-gateway_{{version.Replace('-', '~')}}_amd64.deb", "sha256": "{{new string('b', 64)}}", "size": 5678 }
          }
        }
        """);

    private string Sign(byte[] manifest, ECDsa? key = null) =>
        Convert.ToBase64String((key ?? _key).SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    /// <summary>Выпуск ленты в памяти: манифест и подпись (нет — не подписан), без ссылок на файлы.</summary>
    private ReleaseEntry Entry(string version, byte[] manifest, bool signed = true, string channel = "stable", ECDsa? key = null) =>
        new(version, channel == "beta" ? UpdateChannel.Beta : UpdateChannel.Stable, manifest, signed ? Sign(manifest, key) : null, new Dictionary<string, Uri>());

    /// <summary>Выпуск в ленте сайта: манифест — base64 точных байт, подпись — текст update.json.sig.</summary>
    private static string Release(string version, byte[] manifest, string signature, string channel = "stable", string files = "{}") =>
        $$"""{"version":"{{version}}","channel":"{{channel}}","manifest":"{{Convert.ToBase64String(manifest)}}","signature":{{System.Text.Json.JsonSerializer.Serialize(signature)}},"files":{{files}}}""";

    private static byte[] SiteFeed(params string[] releases) => Encoding.UTF8.GetBytes($$"""{"releases":[{{string.Join(',', releases)}}]}""");
}
