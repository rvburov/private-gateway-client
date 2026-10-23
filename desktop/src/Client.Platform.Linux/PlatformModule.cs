using System.Runtime.Versioning;

namespace PrivateGateway.Client.Platform.Linux;

/// <summary>
/// Платформенная часть службы для Linux: nftables, systemd, systemd-resolved, polkit.
/// Код здесь вызывается только службой; интерфейс на этот проект не ссылается (тесты Architecture).
/// </summary>
[SupportedOSPlatform("linux")]
public static class PlatformModule
{
    public const string OperatingSystem = "linux";
}
