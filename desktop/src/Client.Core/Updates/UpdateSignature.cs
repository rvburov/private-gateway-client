using System.Security.Cryptography;

namespace PrivateGateway.Client.Core.Updates;

/// <summary>
/// Подпись манифеста обновления: <c>update.json.sig</c> — ECDSA P-256 над точными байтами <c>update.json</c>, SHA-256,
/// DER в base64 (scripts/release.cs). Проверяется открытыми ключами программы (shared/update/keys): подходит любой —
/// так меняется ключ без потери прежних выпусков.
/// </summary>
public static class UpdateSignature
{
    /// <summary>Подпись сходится хотя бы с одним ключом P-256; испорченная подпись или ключ — не сходится.</summary>
    public static bool Verify(ReadOnlySpan<byte> manifest, string signature, IEnumerable<string> publicKeys)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(publicKeys);
        byte[] der;
        try
        {
            der = Convert.FromBase64String(signature.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        foreach (var pem in publicKeys)
        {
            using var key = ECDsa.Create();
            try
            {
                key.ImportFromPem(pem);
            }
            catch (Exception e) when (e is ArgumentException or CryptographicException)
            {
                continue;
            }

            if (key.KeySize == 256 && key.VerifyData(manifest, der, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            {
                return true;
            }
        }

        return false;
    }
}
