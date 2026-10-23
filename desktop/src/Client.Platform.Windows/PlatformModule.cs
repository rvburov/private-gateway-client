using System.Runtime.Versioning;

namespace PrivateGateway.Client.Platform.Windows;

/// <summary>
/// Платформенная часть службы для Windows: WFP, Wintun, служба Windows.
/// Код здесь вызывается только службой; интерфейс на этот проект не ссылается (тесты Architecture).
/// </summary>
[SupportedOSPlatform("windows")]
public static class PlatformModule
{
    public const string OperatingSystem = "windows";
}
