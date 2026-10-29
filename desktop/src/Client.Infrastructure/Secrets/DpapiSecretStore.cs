using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using PrivateGateway.Client.Application.Secrets;

namespace PrivateGateway.Client.Infrastructure.Secrets;

/// <summary>
/// Windows: секрет шифруется DPAPI текущего пользователя и лежит в своем файле <see cref="SecretFile"/> (secrets.db).
/// Расшифровать его может только этот пользователь на этом компьютере. Дополнительная энтропия — ключ записи:
/// зашифрованное значение нельзя переставить под другой ключ.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore(SecretFile file) : ISecretStore
{
    public string Name => "Windows DPAPI";

    public bool IsAvailable => true;

    public void Save(string key, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        file.Write(key, ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy(key), DataProtectionScope.CurrentUser));
    }

    public string? Read(string key)
    {
        if (file.Read(key) is not { } blob)
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, Entropy(key), DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException e)
        {
            // Другой пользователь или переустановленная Windows: прочитать нельзя, и это ошибка, а не пустой секрет.
            throw new InvalidOperationException("DPAPI не расшифровал секрет", e);
        }
    }

    public void Delete(string key) => file.Delete(key);

    private static byte[] Entropy(string key) => Encoding.UTF8.GetBytes("PrivateGateway:" + key);
}
