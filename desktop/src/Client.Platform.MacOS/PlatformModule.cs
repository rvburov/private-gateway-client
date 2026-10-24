using System.Runtime.Versioning;

namespace PrivateGateway.Client.Platform.MacOS;

/// <summary>
/// Платформенная часть службы для MacOS: Network Extension или launchd + utun + pf (решение на спайке).
/// Код здесь вызывается только службой; интерфейс на этот проект не ссылается (тесты Architecture).
/// </summary>
[SupportedOSPlatform("macos")]
public static class PlatformModule
{
    public const string OperatingSystem = "macos";
}
