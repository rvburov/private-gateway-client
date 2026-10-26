using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Platform.MacOS;

/// <summary>
/// Шифрование секретов установленной службы macOS: ключ AES-256 — в системной Связке ключей (<see cref="SystemKeychain"/>:
/// менять и читать ее может только root, на диске она зашифрована ключом системы), секрет шифруется им (AES-GCM; имя секрета
/// входит в шифр — под другим именем не открыть). Ключ программе <c>security</c> передается через stdin (<c>security -i</c>),
/// а не параметром процесса; доверенная программа записи — <c>/usr/bin/security</c> (подписана Apple), поэтому
/// обновление службы доступа к ключу не меняет.
/// </summary>
/// <param name="security">Вызов security: аргументы и stdin → код выхода, вывод и текст ошибок.</param>
/// <param name="name">Имя секрета — учетная запись записи в Связке ключей.</param>
/// <param name="keychain">Связка ключей; тесты дают свою.</param>
public sealed class KeychainSealer(
    Func<IReadOnlyList<string>, string?, (int Code, string Output, string Error)> security,
    string name,
    string keychain = KeychainSealer.SystemKeychain) : ISecretSealer
{
    /// <summary>Системная Связка ключей macOS.</summary>
    public const string SystemKeychain = "/Library/Keychains/System.keychain";

    private const string Program = "/usr/bin/security";
    private const string Service = "PrivateGateway";

    // Код выхода security, когда записи нет (errSecItemNotFound).
    private const int NotFound = 44;

    // Версия, nonce AES-GCM, тег, шифр.
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Header = 1 + NonceSize + TagSize;

    public string Name => "Связка ключей macOS";

    /// <summary>security этой машины и системная Связка ключей; не macOS — null.</summary>
    public static KeychainSealer? ForThisMachine(string name) =>
        OperatingSystem.IsMacOS() && File.Exists(Program) ? new KeychainSealer(Run, name) : null;

    public byte[] Seal(byte[] plain)
    {
        ArgumentNullException.ThrowIfNull(plain);
        var key = Key(out var code) ?? (code == NotFound ? CreateKey() : throw new IOException($"Связка ключей macOS не отдала ключ ({code})"));
        var result = new byte[Header + plain.Length];
        result[0] = Version;
        RandomNumberGenerator.Fill(result.AsSpan(1, NonceSize));
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(result.AsSpan(1, NonceSize), plain, result.AsSpan(Header), result.AsSpan(1 + NonceSize, TagSize), Encoding.UTF8.GetBytes(name));
        return result;
    }

    public byte[]? Unseal(byte[] sealedData)
    {
        ArgumentNullException.ThrowIfNull(sealedData);
        if (sealedData.Length < Header || sealedData[0] != Version || Key(out _) is not { } key)
        {
            return null;
        }

        var plain = new byte[sealedData.Length - Header];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(sealedData.AsSpan(1, NonceSize), sealedData.AsSpan(Header), sealedData.AsSpan(1 + NonceSize, TagSize), plain, Encoding.UTF8.GetBytes(name));
            return plain;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Удалить ключ (удаление службы): зашифрованное им больше не открыть; ключа нет — ничего.</summary>
    public void Forget()
    {
        Check();
        _ = security(["delete-generic-password", "-s", Service, "-a", name, keychain], null);
    }

    /// <summary>Ключ из Связки ключей; нет его или он непонятный — null, <paramref name="code"/> — код выхода security.</summary>
    private byte[]? Key(out int code)
    {
        Check();
        (code, var output, _) = security(["find-generic-password", "-s", Service, "-a", name, "-w", keychain], null);
        var hex = output.Trim();
        return code == 0 && hex.Length == 64 && hex.All(char.IsAsciiHexDigit) ? Convert.FromHexString(hex) : null;
    }

    private byte[] CreateKey()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        _ = security(["-i"], $"add-generic-password -s {Service} -a {name} -w {Convert.ToHexStringLower(key)} {keychain}\n");
        return Key(out var code) is { } stored && stored.AsSpan().SequenceEqual(key)
            ? key
            : throw new IOException($"Связка ключей macOS не сохранила ключ ({code})");
    }

    // Имя и путь идут в команду security -i строкой — только буквы, цифры и знаки пути без пробелов.
    private void Check()
    {
        if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            || keychain.Length == 0 || !keychain.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/'))
        {
            throw new ArgumentException($"Недопустимое имя секрета или путь Связки ключей: {name} {keychain}");
        }
    }

    private static (int Code, string Output, string Error) Run(IReadOnlyList<string> arguments, string? input)
    {
        var start = new ProcessStartInfo(Program)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start) ?? throw new IOException("security не запустился");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.StandardInput.Write(input ?? string.Empty);
            process.StandardInput.Close();
            process.WaitForExit();
            return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult().Trim());
        }
        catch (Win32Exception e)
        {
            throw new IOException("security не запустился: " + e.Message, e);
        }
    }
}
