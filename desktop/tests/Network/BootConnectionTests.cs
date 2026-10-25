using System.Net;
using System.Text;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Platform.Linux;
using PrivateGateway.Client.Platform.Windows;
using PrivateGateway.Client.Service;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Последнее подключение для «Защиты при загрузке»: ключи сервера — только зашифрованными хранилищем ОС
/// (Windows — DPAPI, Linux — systemd-creds), испорченный или чужой файл — нет подключения.
/// </summary>
public sealed class BootConnectionTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "pg-bootc-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Connection_comes_back_whole_and_the_key_is_never_plain_on_disk()
    {
        var sealer = OsSealer();
        var path = Path.Combine(_root, "boot-connect.sealed");
        var (definition, _) = XrayKit.Pair("vless", 443);
        var request = ServiceIpcTests.Request(definition, 10808);
        IPAddress[] addresses = [IPAddress.Parse("198.51.100.20"), IPAddress.Parse("2001:db8::20")];

        new BootConnection(path, sealer).Save(request, addresses);

        var bytes = File.ReadAllBytes(path);
        Encoding.UTF8.GetString(bytes).ShouldNotContain(XrayKit.Uuid, customMessage: "ключ сервера — только зашифрованным");
        Encoding.UTF8.GetString(bytes).ShouldNotContain("198.51.100.20");
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(path).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite, "читать файл может только служба");
        }

        var (loaded, known) = new BootConnection(path, sealer).Load().ShouldNotBeNull();
        loaded.ShouldBe(request);
        known.ShouldBe(addresses);

        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        new BootConnection(path, sealer).Load().ShouldBeNull("испорченный файл — без подключения");

        new BootConnection(path, sealer).Forget();
        File.Exists(path).ShouldBeFalse();
        new BootConnection(path, sealer).Load().ShouldBeNull();
    }

    [Fact]
    public void Linux_secret_of_another_name_does_not_open()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux() && Environment.IsPrivilegedProcess && SystemdCredsSealer.ForThisMachine("pg-test") is not null,
            "нужны Linux, root и systemd-creds");
        var sealedData = SystemdCredsSealer.ForThisMachine("pg-test-a")!.Seal("секрет"u8.ToArray());

        SystemdCredsSealer.ForThisMachine("pg-test-a")!.Unseal(sealedData).ShouldBe("секрет"u8.ToArray());
        SystemdCredsSealer.ForThisMachine("pg-test-b")!.Unseal(sealedData).ShouldBeNull("имя секрета входит в шифр");
    }

    /// <summary>Хранилище ОС этой машины, как у службы; нет его — тест пропускается.</summary>
    internal static ISecretSealer OsSealer()
    {
        if (OperatingSystem.IsWindows())
        {
            return new DpapiSealer();
        }

        Assert.SkipUnless(OperatingSystem.IsLinux() && Environment.IsPrivilegedProcess, "systemd-creds шифрует ключом машины — нужен root на Linux");
        return SystemdCredsSealer.ForThisMachine("pg-test-boot") ?? throw new InvalidOperationException("нет systemd-creds");
    }
}
