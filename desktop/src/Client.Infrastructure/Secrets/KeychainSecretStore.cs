using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using PrivateGateway.Client.Application.Secrets;

namespace PrivateGateway.Client.Infrastructure.Secrets;

/// <summary>
/// macOS: секрет — пароль приложения в Связке ключей пользователя (служба «PrivateGateway»,
/// учетная запись — ключ записи). Функции SecKeychain*GenericPassword объявлены устаревшими, но
/// работают во всех поддерживаемых macOS и не требуют сборки словарей CoreFoundation.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class KeychainSecretStore : ISecretStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private static readonly byte[] Service = Encoding.UTF8.GetBytes("PrivateGateway");

    public string Name => "Связка ключей macOS";

    public bool IsAvailable => true;

    public void Save(string key, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var account = Encoding.UTF8.GetBytes(key);
        var data = Encoding.UTF8.GetBytes(secret);
        var found = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
            out _, out var existing, out var item);
        try
        {
            var status = found switch
            {
                ErrSecSuccess => SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)data.Length, data),
                ErrSecItemNotFound => SecKeychainAddGenericPassword(IntPtr.Zero, (uint)Service.Length, Service,
                    (uint)account.Length, account, (uint)data.Length, data, out item),
                _ => found,
            };
            Check(status, "сохранить");
        }
        finally
        {
            Free(existing, item);
        }
    }

    public string? Read(string key)
    {
        var account = Encoding.UTF8.GetBytes(key);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
            out var length, out var data, out var item);
        try
        {
            if (status == ErrSecItemNotFound)
            {
                return null;
            }

            Check(status, "прочитать");
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, (int)length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            Free(data, item);
        }
    }

    public void Delete(string key)
    {
        var account = Encoding.UTF8.GetBytes(key);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
            out _, out var data, out var item);
        try
        {
            if (status == ErrSecItemNotFound)
            {
                return;
            }

            Check(status, "найти");
            Check(SecKeychainItemDelete(item), "удалить");
        }
        finally
        {
            Free(data, item);
        }
    }

    private static void Check(int status, string action)
    {
        if (status != ErrSecSuccess)
        {
            throw new InvalidOperationException($"Связка ключей: не удалось {action} секрет (код {status})");
        }
    }

    private static void Free(IntPtr data, IntPtr item)
    {
        if (data != IntPtr.Zero)
        {
            _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
        }

        if (item != IntPtr.Zero)
        {
            CFRelease(item);
        }
    }

    [LibraryImport(Security)]
    private static partial int SecKeychainFindGenericPassword(
        IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName,
        out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainAddGenericPassword(
        IntPtr keychain, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName,
        uint passwordLength, byte[] passwordData, out IntPtr itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemDelete(IntPtr itemRef);

    [LibraryImport(Security)]
    private static partial int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr cf);
}
