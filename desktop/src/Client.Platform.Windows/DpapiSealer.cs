using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Platform.Windows;

/// <summary>
/// DPAPI от имени учетной записи службы (SYSTEM): расшифровать может только она. Другие пользователи машины
/// файл прочитать могут, расшифровать — нет; администраторы — вне модели угроз.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSealer : ISecretSealer
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PrivateGateway.Service.Sealed");

    public string Name => "DPAPI";

    public byte[] Seal(byte[] plain)
    {
        try
        {
            return ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException e)
        {
            throw new IOException("DPAPI не зашифровал: " + e.Message, e);
        }
    }

    public byte[]? Unseal(byte[] sealedData)
    {
        try
        {
            return ProtectedData.Unprotect(sealedData, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
