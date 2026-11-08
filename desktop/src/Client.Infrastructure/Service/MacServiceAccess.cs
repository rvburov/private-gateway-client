using System.Runtime.InteropServices;
using System.Text;
using PrivateGateway.Client.Application.Connection;

namespace PrivateGateway.Client.Infrastructure.Service;

/// <summary>
/// Разрешение уже установленного LaunchDaemon: штатный SMAppService macOS 13+, без запуска команд,
/// чтения журналов и изменения регистрации. Разрешение отозвано — только пользователь возвращает его в настройках ОС.
/// </summary>
public sealed partial class MacServiceAccess(Func<bool> installed, Func<nint> status, Action openSettings) : IServiceAccess
{
    public const string PlistPath = "/Library/LaunchDaemons/io.github.rvburov.private-gateway.plist";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly Lazy<nint> Framework = new(() => NativeLibrary.Load(
        "/System/Library/Frameworks/ServiceManagement.framework/ServiceManagement"));

    public bool IsSupported => true;

    public static IServiceAccess ForThisMachine(string plistPath = PlistPath) => OperatingSystem.IsMacOSVersionAtLeast(13)
        ? new MacServiceAccess(() => File.Exists(plistPath), () => ReadNativeStatus(plistPath), OpenNativeSettings)
        : new NoServiceAccess();

    public ServiceAccess Read()
    {
        try
        {
            if (!installed())
            {
                return ServiceAccess.NotInstalled;
            }

            // SMAppServiceStatusRequiresApproval = 2. Остальные ответы не подтверждают запрет.
            return status() switch
            {
                2 => ServiceAccess.RequiresApproval,
                0 or 1 or 3 => ServiceAccess.Installed,
                _ => ServiceAccess.Unknown,
            };
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ServiceAccess.Unknown;
        }
    }

    public bool OpenSettings()
    {
        try
        {
            openSettings();
            return true;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    private static nint ServiceClass()
    {
        _ = Framework.Value;
        var type = GetClass("SMAppService");
        return type != 0 ? type : throw new InvalidOperationException("SMAppService недоступен");
    }

    private static nint ReadNativeStatus(string plistPath)
    {
        var type = ServiceClass();
        var pool = SendPointer(GetClass("NSAutoreleasePool"), Selector("new"));
        try
        {
            return ReadUrlStatus(type, plistPath);
        }
        finally
        {
            SendVoid(pool, Selector("drain"));
        }
    }

    private static nint ReadUrlStatus(nint type, string plistPath)
    {
        var bytes = Encoding.UTF8.GetBytes(plistPath);
        var url = CreateUrl(0, bytes, bytes.Length, 0);
        if (url == 0)
        {
            throw new InvalidOperationException("Не удалось создать URL службы");
        }

        try
        {
            return SendStatus(type, Selector("statusForLegacyURL:"), url);
        }
        finally
        {
            Release(url);
        }
    }

    // Вызывается командой окна на главном потоке; открывает раздел, не меняет разрешение.
    private static void OpenNativeSettings() => SendVoid(ServiceClass(), Selector("openSystemSettingsLoginItems"));

    [LibraryImport(ObjC, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetClass(string name);

    [LibraryImport(ObjC, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint Selector(string name);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendStatus(nint receiver, nint selector, nint url);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial nint SendPointer(nint receiver, nint selector);

    [LibraryImport(ObjC, EntryPoint = "objc_msgSend")]
    private static partial void SendVoid(nint receiver, nint selector);

    [LibraryImport(CoreFoundation, EntryPoint = "CFURLCreateFromFileSystemRepresentation")]
    private static partial nint CreateUrl(nint allocator, byte[] bytes, nint length, byte isDirectory);

    [LibraryImport(CoreFoundation, EntryPoint = "CFRelease")]
    private static partial void Release(nint value);
}
