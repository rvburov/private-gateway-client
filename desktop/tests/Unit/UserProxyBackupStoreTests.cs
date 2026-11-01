using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Infrastructure.Service;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Данные только в своем временном файле; настройки Windows не читаются и не меняются.</summary>
public sealed class UserProxyBackupStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pg-window-proxy-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(ServiceKind.User, true)]
    [InlineData(ServiceKind.Installed, false)]
    public void Only_a_user_service_backup_requests_automatic_user_service_recovery(ServiceKind owner, bool recover)
    {
        var path = Path.Combine(_folder, "proxy.json");
        var original = new SystemProxySnapshot(new Dictionary<string, string?> { ["ProxyEnable"] = "0" });
        var applied = CurrentUserWindowsProxy.ForPort(2080);
        new FileUserProxyBackupStore(path).Write(new UserProxyBackup(original, applied, 2080, AppliedConfirmed: true, OwnerService: owner));

        var reloaded = new FileUserProxyBackupStore(path);
        reloaded.IsUserServiceBackup().ShouldBe(recover, "Off помощника не должен подменять неизвестное состояние установленной службы");
        reloaded.Read()!.Original.Values["ProxyEnable"].ShouldBe("0");
        reloaded.Read()!.Applied.Values["ProxyServer"].ShouldBe("127.0.0.1:2080");
    }

    [Fact]
    public void A_corrupted_backup_does_not_launch_an_unrelated_service()
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, "proxy.json");
        File.WriteAllText(path, "{\"Port\":2080,\"OwnerService\":2}");
        new FileUserProxyBackupStore(path).IsUserServiceBackup().ShouldBeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }

        GC.SuppressFinalize(this);
    }
}
