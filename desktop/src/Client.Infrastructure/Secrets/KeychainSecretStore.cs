using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using PrivateGateway.Client.Application.Secrets;

namespace PrivateGateway.Client.Infrastructure.Secrets;

/// <summary>
/// Нативный доступ macOS: старые отдельные записи — служба «PrivateGateway», общий ключ — «PrivateGateway.Vault».
/// Разные службы не позволяют удалению старой записи затронуть ключ нового файла.
/// Функции SecKeychain*GenericPassword объявлены устаревшими, но
/// работают во всех поддерживаемых macOS и не требуют сборки словарей CoreFoundation.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class KeychainSecretStore : ISecretStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int ErrSecSuccess = 0;
    private const int ErrSecItemNotFound = -25300;
    private const int ErrSecDuplicateItem = -25299;
    private const string VaultKeyName = "vault-master-v1";
    private static readonly byte[] Service = Encoding.UTF8.GetBytes("PrivateGateway");
    private static readonly byte[] VaultService = Encoding.UTF8.GetBytes("PrivateGateway.Vault");

    public string Name => "Связка ключей macOS";

    public bool IsAvailable => true;

    public void Save(string key, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var account = Encoding.UTF8.GetBytes(key);
        var data = Encoding.UTF8.GetBytes(secret);
        // Для изменения нужен идентификатор записи, а не прежний пароль. Чтение пароля здесь вызывало лишнее
        // подтверждение Связки ключей при каждом сохранении, хотя новый секрет уже есть у программы.
        var found = FindItem(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
            IntPtr.Zero, IntPtr.Zero, out var item);
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
            Free(IntPtr.Zero, item);
            CryptographicOperations.ZeroMemory(data);
        }
    }

    public string? Read(string key) => Read(Service, key);

    private static string? Read(byte[] service, string key)
    {
        var account = Encoding.UTF8.GetBytes(key);
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service, (uint)account.Length, account,
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
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            Free(data, item);
        }
    }

    public void Delete(string key)
    {
        var account = Encoding.UTF8.GetBytes(key);
        var status = FindItem(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
            IntPtr.Zero, IntPtr.Zero, out var item);
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
            Free(IntPtr.Zero, item);
        }
    }

    /// <summary>
    /// Один ключ зашифрованного файла пользователя. При гонке двух процессов уже созданный ключ не заменяется.
    /// Отказ Связки ключей не считается отсутствием ключа; чтение существующего файла новый ключ не создает.
    /// </summary>
    public static string? VaultKey(bool create)
    {
        var existing = Read(VaultService, VaultKeyName);
        if (existing is not null || !create)
        {
            return existing;
        }

        var account = Encoding.UTF8.GetBytes(VaultKeyName);
        var key = RandomNumberGenerator.GetBytes(32);
        var encoded = Convert.ToBase64String(key);
        var data = Encoding.UTF8.GetBytes(encoded);
        try
        {
            var status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)VaultService.Length, VaultService,
                (uint)account.Length, account, (uint)data.Length, data, out var item);
            Free(IntPtr.Zero, item);
            if (status == ErrSecDuplicateItem)
            {
                return Read(VaultService, VaultKeyName);
            }

            Check(status, "сохранить ключ хранилища");
            return encoded;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(data);
        }
    }

    private static void Check(int status, string action)
    {
        if (status != ErrSecSuccess)
        {
            throw new SecretStoreException($"Связка ключей: не удалось {action} секрет (код {status})");
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

    [LibraryImport(Security, EntryPoint = "SecKeychainFindGenericPassword")]
    private static partial int FindItem(
        IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName, uint accountNameLength, byte[] accountName,
        IntPtr passwordLength, IntPtr passwordData, out IntPtr itemRef);

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
