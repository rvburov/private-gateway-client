using System.Security.Cryptography;
using System.Text.Json;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Core.Updates;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Updates;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>История подписанных политик — на настоящем диске, ключ и ленты создает тест; сети не требуется.</summary>
public sealed class UpdatePolicyHistoryTests : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _state = Directory.CreateTempSubdirectory("pg-update-policy-").FullName;

    public void Dispose()
    {
        _key.Dispose();
        Directory.Delete(_state, recursive: true);
    }

    [Fact]
    public void Stop_survives_a_restart_and_resuming_requires_a_newer_signed_revision()
    {
        var running = Manifest(1, 100);
        var stopped = Manifest(2, 0);
        var checker = Checker();
        checker.Check(UpdateChannel.Stable, Feed(running)).State.ShouldBe(UpdateState.Available);
        checker.Check(UpdateChannel.Stable, Feed(stopped)).State.ShouldBe(UpdateState.UpToDate);

        var restarted = Checker();
        restarted.Check(UpdateChannel.Stable, Feed(running)).State.ShouldBe(UpdateState.Failed);
        restarted.Check(UpdateChannel.Stable, Feed(stopped)).State.ShouldBe(UpdateState.UpToDate);
        restarted.Check(UpdateChannel.Stable, Feed(Manifest(3, 100))).State.ShouldBe(UpdateState.Available);
        Checker().Check(UpdateChannel.Stable, Feed(stopped)).State.ShouldBe(UpdateState.Failed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_stop_later_in_the_same_feed_is_observed_before_selecting_an_old_duplicate(bool reverse)
    {
        var running = Manifest(1, 100);
        var stopped = Manifest(2, 0);
        var feed = reverse ? Feed(stopped, running) : Feed(running, stopped);

        Checker().Check(UpdateChannel.Stable, feed).State.ShouldNotBe(UpdateState.Available);
        Checker().Check(UpdateChannel.Stable, Feed(running)).State.ShouldBe(UpdateState.Failed);
    }

    [Fact]
    public void A_policy_outside_the_install_bucket_is_also_remembered()
    {
        Checker(bucket: 99).Check(UpdateChannel.Stable, Feed(Manifest(2, 10))).State.ShouldBe(UpdateState.UpToDate);

        Checker(bucket: 99).Check(UpdateChannel.Stable, Feed(Manifest(1, 100))).State.ShouldBe(UpdateState.Failed);
    }

    [Fact]
    public void Equal_revision_requires_the_same_signed_bytes_but_identical_retries_work()
    {
        var checker = Checker();
        var original = Manifest(1, 100);
        checker.Check(UpdateChannel.Stable, Feed(original)).State.ShouldBe(UpdateState.Available);
        checker.Check(UpdateChannel.Stable, Feed(original)).State.ShouldBe(UpdateState.Available);

        Checker().Check(UpdateChannel.Stable, Feed(Manifest(1, 0))).State.ShouldBe(UpdateState.Failed);
        Checker().Check(UpdateChannel.Stable, Feed(original)).State.ShouldBe(UpdateState.Available);
    }

    [Fact]
    public void Invalid_signature_cannot_poison_the_saved_revision()
    {
        using var foreign = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var poisoned = FeedWithKey(foreign, Manifest(1000, 0));
        Checker().Check(UpdateChannel.Stable, poisoned).State.ShouldBe(UpdateState.Failed);

        Checker().Check(UpdateChannel.Stable, Feed(Manifest(1, 100))).State.ShouldBe(UpdateState.Available);
    }

    [Fact]
    public void Legacy_revision_zero_is_accepted_once_and_cannot_replace_an_observed_revision()
    {
        var legacy = Manifest(null, 100);
        Checker().Check(UpdateChannel.Stable, Feed(legacy)).State.ShouldBe(UpdateState.Available);
        Checker().Check(UpdateChannel.Stable, Feed(legacy)).State.ShouldBe(UpdateState.Available);
        Checker().Check(UpdateChannel.Stable, Feed(Manifest(1, 0))).State.ShouldBe(UpdateState.UpToDate);

        Checker().Check(UpdateChannel.Stable, Feed(legacy)).State.ShouldBe(UpdateState.Failed);
    }

    [Fact]
    public void Corrupted_history_is_not_silently_reset_and_does_not_allow_an_update()
    {
        File.WriteAllText(Path.Combine(_state, UpdatePolicyHistory.FileName), "не JSON");

        Checker().Check(UpdateChannel.Stable, Feed(Manifest(1, 100))).State.ShouldBe(UpdateState.Failed);
        File.ReadAllText(Path.Combine(_state, UpdatePolicyHistory.FileName)).ShouldBe("не JSON");
    }

    [Fact]
    public void Failed_write_refuses_the_update_and_keeps_the_new_revision_for_the_retry()
    {
        var checker = Checker();
        var path = Path.Combine(_state, UpdatePolicyHistory.FileName);
        Directory.CreateDirectory(path); // Файл атомарно заменить папкой нельзя на любой ОС.

        checker.Check(UpdateChannel.Stable, Feed(Manifest(2, 100))).State.ShouldBe(UpdateState.Failed);
        checker.Check(UpdateChannel.Stable, Feed(Manifest(1, 100))).State.ShouldBe(UpdateState.Failed);
        Directory.Delete(path);
        checker.Check(UpdateChannel.Stable, Feed(Manifest(2, 100))).State.ShouldBe(UpdateState.Available);
        Checker().Check(UpdateChannel.Stable, Feed(Manifest(1, 100))).State.ShouldBe(UpdateState.Failed);
    }

    [Fact]
    public void A_download_rechecks_revision_even_when_the_old_policy_has_a_valid_signature()
    {
        var checker = Checker();
        checker.Check(UpdateChannel.Stable, Feed(Manifest(1, 100))).State.ShouldBe(UpdateState.Available);
        checker.Link("0.3.0", "win-x64-msi", Feed(Manifest(2, 0))).ShouldBeNull();

        checker.Link("0.3.0", "win-x64-msi", Feed(Manifest(1, 100))).ShouldBeNull();
        Checker().Link("0.3.0", "win-x64-msi", Feed(Manifest(1, 100))).ShouldBeNull();
        checker.Link("0.3.0", "win-x64-msi", Feed(Manifest(3, 100))).ShouldNotBeNull();
    }

    [Fact]
    public void Intentional_binary_rollback_does_not_allow_an_older_policy_for_that_release()
    {
        var checker = Checker(current: "0.4.0", history: new VersionHistory("0.3.0", null));
        var stopped = Feed(Manifest(2, 0));
        checker.Check(UpdateChannel.Stable, stopped).Rollback.ShouldBe("0.3.0", "свежая подписанная политика не запрещает предусмотренный откат");
        checker.Link("0.3.0", "win-x64-msi", stopped).ShouldNotBeNull();

        Checker(current: "0.4.0", history: new VersionHistory("0.3.0", null))
            .Check(UpdateChannel.Stable, Feed(Manifest(1, 100))).Rollback.ShouldBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exact_version_rollback_ignores_the_feed_channel_but_keeps_the_signed_policy_history(bool reverse)
    {
        var older = Entry(Manifest(1, 100));
        var newer = Entry(Manifest(2, 0));
        ReleaseEntry[] releases = reverse ? [newer, older] : [older, newer];
        var policies = new UpdatePolicyHistory(_state);
        var keys = new[] { _key.ExportSubjectPublicKeyInfoPem() };

        var found = UpdateFinder.Release(releases, keys, "0.3.0", policies.Accept);

        found.Manifest.ShouldNotBeNull();
        found.Manifest.Revision.ShouldBe(2, "откат берет актуальную политику даже при другой внешней метке канала");
        found.Manifest.Channel.ShouldBe(UpdateChannel.Stable, "история привязана к подписанному каналу");
        UpdateFinder.Release([older], keys, "0.3.0", new UpdatePolicyHistory(_state).Accept).Manifest.ShouldBeNull();

        ReleaseEntry Entry(byte[] manifest) => new("0.3.0", UpdateChannel.Beta, manifest,
            Convert.ToBase64String(_key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)),
            new Dictionary<string, Uri>());
    }

    private UpdateChecker Checker(int bucket = 0, string current = "0.2.0", VersionHistory? history = null) =>
        new([_key.ExportSubjectPublicKeyInfoPem()], current, bucket, TimeProvider.System, new ServiceJournal(TimeProvider.System), history,
            "win-x64-msi", policies: new UpdatePolicyHistory(_state));

    private byte[] Feed(params byte[][] manifests) => FeedWithKey(_key, manifests);

    private static byte[] FeedWithKey(ECDsa key, params byte[][] manifests) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        releases = manifests.Select(bytes => new
        {
            version = "0.3.0",
            channel = "stable",
            manifest = Convert.ToBase64String(bytes),
            signature = Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)),
            files = new Dictionary<string, object>
            {
                ["win-x64-msi"] = new { url = "https://private-gateway.duckdns.org/api/app/updates/files/test-ticket" },
            },
        }),
    });

    private static byte[] Manifest(long? revision, int rollout)
    {
        var manifest = new Dictionary<string, object>
        {
            ["version"] = "0.3.0",
            ["channel"] = "stable",
            ["rollout"] = rollout,
            ["published"] = "2026-11-14T08:00:00Z",
            ["notes"] = "https://example.com/downloads",
            ["assets"] = new Dictionary<string, object>
            {
                ["win-x64-msi"] = new { file = "PrivateGateway-0.3.0-win-x64.msi", sha256 = new string('a', 64), size = 100 },
            },
        };
        if (revision is { } value)
        {
            manifest["revision"] = value;
        }

        return JsonSerializer.SerializeToUtf8Bytes(manifest);
    }
}
