using System.Security.Cryptography;
using System.Text;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Core.Updates;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// Обновление программы: манифест выпуска (<see cref="UpdateManifest"/>), его подпись (<see cref="UpdateSignature"/>),
/// список выпусков GitHub и выбор выпуска (<see cref="UpdateFinder"/>) — канал, версия, подпись, доля раскатки.
/// Ключи — созданные здесь же, как их создает scripts/release.cs keygen.
/// </summary>
public sealed class UpdateFinderTests : IDisposable
{
    private static readonly Uri Downloads = new("https://github.com/rvburov/private-gateway-client/releases/download/");

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
        manifest.Notes.ShouldBe(new Uri("https://github.com/rvburov/private-gateway-client/releases/tag/v0.3.0"));
        manifest.Highlights.ShouldBe(["Быстрее переподключение после сна"]);
        manifest.Assets["win-x64-msi"].ShouldBe(new UpdateAsset("PrivateGateway-0.3.0-win-x64.msi", new string('a', 64), 1234));
        manifest.Assets["linux-x64-deb"].File.ShouldBe("private-gateway_0.3.0_amd64.deb");
    }

    [Theory]
    [InlineData("\"version\": \"0.3.0\"", "\"version\": \"v0.3.0\"")]
    [InlineData("\"channel\": \"stable\"", "\"channel\": \"nightly\"")]
    [InlineData("\"rollout\": 20", "\"rollout\": 0")]
    [InlineData("\"rollout\": 20", "\"rollout\": 101")]
    [InlineData("https://github.com/rvburov", "http://github.com/rvburov")]
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
    public void Release_list_takes_tagged_releases_and_only_this_repository_downloads()
    {
        var json = """
            [
              { "tag_name": "v0.3.0", "draft": false, "prerelease": false, "assets": [
                  { "name": "update.json", "browser_download_url": "https://github.com/rvburov/private-gateway-client/releases/download/v0.3.0/update.json" },
                  { "name": "update.json.sig", "browser_download_url": "https://github.com/rvburov/private-gateway-client/releases/download/v0.3.0/update.json.sig" } ] },
              { "tag_name": "v0.4.0-beta.1", "draft": false, "prerelease": true, "assets": [
                  { "name": "update.json", "browser_download_url": "https://example.com/rvburov/private-gateway-client/releases/download/v0.4.0-beta.1/update.json" },
                  { "name": "update.json.sig", "browser_download_url": "https://github.com/rvburov/private-gateway-client/releases/download/v0.3.0/update.json.sig" } ] },
              { "tag_name": "v0.5.0", "draft": true, "prerelease": false, "assets": [] },
              { "tag_name": "nightly", "draft": false, "prerelease": true, "assets": [] }
            ]
            """;

        var releases = ReleaseEntry.ParseGitHub(Encoding.UTF8.GetBytes(json), Downloads);

        releases.Select(r => r.Version).ShouldBe(["0.3.0", "0.4.0-beta.1"], "черновик и тег не v<SemVer> — не выпуски");
        releases[0].Manifest.ShouldBe(new Uri(Downloads, "v0.3.0/update.json"));
        releases[0].Signature.ShouldNotBeNull();
        releases[1].Prerelease.ShouldBeTrue();
        releases[1].Manifest.ShouldBeNull("загрузка не с GitHub этого репозитория");
        releases[1].Signature.ShouldBeNull("подпись от другого выпуска");
        Should.Throw<FormatException>(() => ReleaseEntry.ParseGitHub("{\"message\": \"API rate limit exceeded\"}"u8, Downloads));
    }

    [Fact]
    public async Task Newer_signed_release_of_the_channel_is_found()
    {
        var feed = new Feed(_key).Add("0.3.0", Manifest("0.3.0"), signed: true).Add("0.2.0", Manifest("0.2.0"), signed: true);

        var search = await UpdateFinder.FindAsync(feed, [PublicKey], "0.2.0", UpdateChannel.Stable, bucket: 50, TestContext.Current.CancellationToken);

        search.State.ShouldBe(UpdateState.Available);
        search.Manifest!.Version.ShouldBe("0.3.0");
        search.Skipped.ShouldBeEmpty();
    }

    [Fact]
    public async Task Same_or_older_version_is_up_to_date_and_nothing_is_downloaded()
    {
        var feed = new Feed(_key).Add("0.2.0", Manifest("0.2.0"), signed: true).Add("0.1.0", Manifest("0.1.0"), signed: true);

        var search = await UpdateFinder.FindAsync(feed, [PublicKey], "0.2.0", UpdateChannel.Stable, bucket: 0, TestContext.Current.CancellationToken);

        search.State.ShouldBe(UpdateState.UpToDate);
        feed.Downloads.ShouldBe(0);
    }

    [Fact]
    public async Task Without_program_keys_releases_are_not_even_listed()
    {
        var feed = new Feed(_key).Add("0.3.0", Manifest("0.3.0"), signed: true);

        (await UpdateFinder.FindAsync(feed, [], "0.2.0", UpdateChannel.Stable, bucket: 0, TestContext.Current.CancellationToken)).State.ShouldBe(UpdateState.NotSigned);
        feed.Listed.ShouldBe(0);
    }

    [Fact]
    public async Task Stable_channel_skips_prereleases_and_beta_takes_them()
    {
        var feed = new Feed(_key)
            .Add("0.4.0-beta.1", Manifest("0.4.0-beta.1", channel: "beta"), signed: true, prerelease: true)
            .Add("0.3.0", Manifest("0.3.0"), signed: true);

        (await UpdateFinder.FindAsync(feed, [PublicKey], "0.2.0", UpdateChannel.Stable, bucket: 0, TestContext.Current.CancellationToken)).Manifest!.Version.ShouldBe("0.3.0");
        feed.Downloads.ShouldBe(2, "предварительный выпуск стабильный канал даже не скачивает");
        (await UpdateFinder.FindAsync(feed, [PublicKey], "0.2.0", UpdateChannel.Beta, bucket: 0, TestContext.Current.CancellationToken)).Manifest!.Version.ShouldBe("0.4.0-beta.1");
    }

    [Fact]
    public async Task Rollout_share_decides_by_the_install_number_and_an_older_release_may_be_taken()
    {
        var feed = new Feed(_key).Add("0.4.0", Manifest("0.4.0", rollout: 20), signed: true).Add("0.3.0", Manifest("0.3.0", rollout: 100), signed: true);

        (await UpdateFinder.FindAsync(feed, [PublicKey], "0.2.0", UpdateChannel.Stable, bucket: 19, TestContext.Current.CancellationToken)).Manifest!.Version.ShouldBe("0.4.0", "номер 19 < 20 %");
        var later = await UpdateFinder.FindAsync(feed, [PublicKey], "0.2.0", UpdateChannel.Stable, bucket: 20, TestContext.Current.CancellationToken);
        later.Manifest!.Version.ShouldBe("0.3.0", "до 0.4.0 раскатка еще не дошла, а 0.3.0 — у всех");
        later.Skipped.ShouldContain(s => s.Contains("раскатка 20 %", StringComparison.Ordinal));
        (await UpdateFinder.FindAsync(feed, [PublicKey], "0.3.0", UpdateChannel.Stable, bucket: 99, TestContext.Current.CancellationToken)).State.ShouldBe(UpdateState.UpToDate);
    }

    [Fact]
    public async Task Unsigned_foreign_signed_or_mismatched_releases_are_skipped_with_a_reason()
    {
        var feed = new Feed(_key)
            .Add("0.6.0", Manifest("0.6.0"), signed: false)
            .Add("0.5.0", Manifest("0.5.0"), signed: true, key: _otherKey)
            .Add("0.4.0", Manifest("0.3.9"), signed: true)
            .Add("0.3.1", Manifest("0.3.1", channel: "beta"), signed: true)
            .Add("0.3.0", Manifest("0.3.0"), signed: true);

        var search = await UpdateFinder.FindAsync(feed, [PublicKey], "0.2.0", UpdateChannel.Stable, bucket: 0, TestContext.Current.CancellationToken);

        search.Manifest!.Version.ShouldBe("0.3.0");
        search.Skipped.Count.ShouldBe(4);
        search.Skipped[0].ShouldContain("без подписанного манифеста");
        search.Skipped[1].ShouldContain("подпись манифеста не сходится");
        search.Skipped[2].ShouldContain("манифест от другого выпуска", customMessage: "подписанный манифест 0.3.9 под тегом 0.4.0 — подмена");
        search.Skipped[3].ShouldContain("манифест от другого выпуска", customMessage: "бета-манифест в стабильном выпуске");
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
        new UpdateOffer(UpdateState.UpToDate, at).ToContract().ToCore().Version.ShouldBeNull();
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

    private static byte[] Manifest(string version, int rollout = 100, string channel = "stable") => Encoding.UTF8.GetBytes($$"""
        {
          "version": "{{version}}",
          "channel": "{{channel}}",
          "rollout": {{rollout}},
          "published": "2026-10-26T08:00:00Z",
          "notes": "https://github.com/rvburov/private-gateway-client/releases/tag/v{{version}}",
          "highlights": [ "Быстрее переподключение после сна" ],
          "assets": {
            "win-x64-msi": { "file": "PrivateGateway-{{version}}-win-x64.msi", "sha256": "{{new string('a', 64)}}", "size": 1234 },
            "linux-x64-deb": { "file": "private-gateway_{{version.Replace('-', '~')}}_amd64.deb", "sha256": "{{new string('b', 64)}}", "size": 5678 }
          }
        }
        """);

    private string Sign(byte[] manifest) =>
        Convert.ToBase64String(_key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    /// <summary>Выпуски «на GitHub» в памяти: манифест и подпись по ссылкам, счетчики обращений.</summary>
    private sealed class Feed(ECDsa signer) : IReleaseFeed
    {
        private readonly List<ReleaseEntry> _releases = [];
        private readonly Dictionary<Uri, byte[]> _files = [];

        public int Listed { get; private set; }

        public int Downloads { get; private set; }

        public Feed Add(string version, byte[] manifest, bool signed, bool prerelease = false, ECDsa? key = null)
        {
            var manifestUrl = new Uri(UpdateFinderTests.Downloads, $"v{version}/update.json");
            var signatureUrl = new Uri(UpdateFinderTests.Downloads, $"v{version}/update.json.sig");
            _files[manifestUrl] = manifest;
            if (signed)
            {
                _files[signatureUrl] = Encoding.ASCII.GetBytes(Convert.ToBase64String((key ?? signer).SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) + "\n");
            }

            _releases.Add(new ReleaseEntry(version, prerelease, manifestUrl, signed ? signatureUrl : null));
            return this;
        }

        public Task<IReadOnlyList<ReleaseEntry>> ListAsync(CancellationToken cancellationToken)
        {
            Listed++;
            return Task.FromResult<IReadOnlyList<ReleaseEntry>>(_releases);
        }

        public Task<byte[]> DownloadAsync(Uri url, int maxBytes, CancellationToken cancellationToken)
        {
            Downloads++;
            return Task.FromResult(_files[url]);
        }
    }
}
