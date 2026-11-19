using System.Security.Cryptography;
using System.Text;

namespace PrivateGateway.Client.Core.Settings;

/// <summary>
/// PIN-код снятия блока сайтов (решение 742): 4–8 цифр. Хранится солью и хешем PBKDF2-SHA256 — сам код не записывается
/// нигде. Без него нельзя ослабить блок: убрать или изменить правило «Блокировать», открыть остальные сайты, разрешить сайт
/// при «Остальные сайты: Блокировать», восстановить копию настроек. Забыли — сброс входом в аккаунт.
/// </summary>
public sealed record BlockPin(string Salt, string Hash)
{
    public const int MinLength = 4;

    public const int MaxLength = 8;

    /// <summary>Сколько раз считать хеш: подбор 4–8 цифр по украденному файлу настроек — не мгновенный.</summary>
    private const int Iterations = 120_000;

    /// <summary>4–8 цифр 0–9.</summary>
    public static bool IsValid(string? pin) => pin is { Length: >= MinLength and <= MaxLength } && pin.All(char.IsAsciiDigit);

    public static BlockPin Create(string pin)
    {
        if (!IsValid(pin))
        {
            throw new ArgumentException("PIN-код — от 4 до 8 цифр", nameof(pin));
        }

        var salt = RandomNumberGenerator.GetBytes(16);
        return new BlockPin(Convert.ToBase64String(salt), Convert.ToBase64String(Derive(pin, salt)));
    }

    /// <summary>Совпадает ли код; сравнение — за одинаковое время.</summary>
    public bool Matches(string? pin)
    {
        if (!IsValid(pin))
        {
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(Derive(pin!, Convert.FromBase64String(Salt)), Convert.FromBase64String(Hash));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Запись для хранилища: «соль:хеш».</summary>
    public override string ToString() => Salt + ":" + Hash;

    /// <summary>Из записи хранилища; пусто или испорчено — null (PIN-кода нет).</summary>
    public static BlockPin? Parse(string? text)
    {
        var parts = text?.Split(':');
        return parts is [{ Length: > 0 } salt, { Length: > 0 } hash] ? new BlockPin(salt, hash) : null;
    }

    private static byte[] Derive(string pin, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, 32);
}
