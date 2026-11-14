using System.Security.Cryptography;
using System.Text;
using PrivateGateway.Client.Application.Secrets;

namespace PrivateGateway.Client.Infrastructure.Secrets;

/// <summary>
/// macOS: один ключ в Связке ключей, записи AES-GCM в отдельном файле. Ключ открывается один раз на процесс,
/// а не при каждом сервере в списке. Прежние отдельные записи переносятся только после разрешенного чтения;
/// отказ не превращается в пустой секрет и не удаляет исходную запись. Старая сборка с другим правом доступа
/// может потребовать отдельного разрешения на каждую еще не перенесенную запись.
/// </summary>
public sealed class KeychainFileSecretStore(SecretFile file, ISecretStore legacy, Func<bool, string?> vaultKey) : ISecretStore, IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int HeaderSize = 4;
    private static readonly byte[] Header = "PGS1"u8.ToArray();
    private readonly Lock _gate = new();
    private byte[]? _key;
    private bool _disposed;

    public string Name => legacy.Name;

    public bool IsAvailable => legacy.IsAvailable;

    public void Save(string key, string secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Write(key, secret);
            Cleanup(key);
        }
    }

    public string? Read(string key)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (file.Read(key) is { } sealedData)
            {
                var opened = Open(key, sealedData);
                Cleanup(key);
                return opened;
            }

            // Не запрашивать ключ файла, если искомого секрета вообще нет. Миграция — по обращению к записи,
            // а не чтение всех серверов при старте окна. Пока запись не сохранена, прежний секрет остается.
            var secret = legacy.Read(key);
            if (secret is null)
            {
                return null;
            }

            Write(key, secret);
            if (Open(key, file.Read(key)!) != secret)
            {
                throw new SecretStoreException("Зашифрованная запись не прошла проверку после переноса");
            }

            Cleanup(key);
            return secret;
        }
    }

    public void Delete(string key)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Остаток прерванной миграции удаляется первым: иначе чтение снова вернуло бы старый секрет.
            legacy.Delete(key);
            file.Delete(key);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_key is not null)
            {
                CryptographicOperations.ZeroMemory(_key);
                _key = null;
            }

            _disposed = true;
        }
    }

    private void Write(string key, string secret)
    {
        var plaintext = Encoding.UTF8.GetBytes(secret);
        try
        {
            var sealedData = new byte[HeaderSize + NonceSize + TagSize + plaintext.Length];
            Header.CopyTo(sealedData, 0);
            var nonce = sealedData.AsSpan(HeaderSize, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            // Если файл уже содержит шифры, отсутствие ключа — ошибка, даже при добавлении другого сервера.
            // Новый ключ сделал бы новую запись читаемой, а все прежние — незаметно потерянными.
            using var aes = new AesGcm(Key(create: !file.HasEntries), TagSize);
            aes.Encrypt(nonce, plaintext, sealedData.AsSpan(HeaderSize + NonceSize + TagSize),
                sealedData.AsSpan(HeaderSize + NonceSize, TagSize), AssociatedData(key));
            file.Write(key, sealedData, cleanupLegacy: file.Read(key) is null);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private string Open(string key, byte[] sealedData)
    {
        if (sealedData.Length < HeaderSize + NonceSize + TagSize || !sealedData.AsSpan(0, HeaderSize).SequenceEqual(Header))
        {
            throw new SecretStoreException("Неизвестный формат зашифрованного секрета");
        }

        var plaintext = new byte[sealedData.Length - HeaderSize - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(Key(create: false), TagSize);
            aes.Decrypt(sealedData.AsSpan(HeaderSize, NonceSize), sealedData.AsSpan(HeaderSize + NonceSize + TagSize),
                sealedData.AsSpan(HeaderSize + NonceSize, TagSize), plaintext, AssociatedData(key));
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException e)
        {
            throw new SecretStoreException("Связка ключей не открыла зашифрованный секрет", e);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private byte[] Key(bool create)
    {
        if (_key is not null)
        {
            return _key;
        }

        var encoded = vaultKey(create) ?? throw new SecretStoreException("В Связке ключей нет ключа зашифрованного хранилища");
        byte[] candidate;
        try
        {
            candidate = Convert.FromBase64String(encoded);
        }
        catch (FormatException e)
        {
            throw new SecretStoreException("Неверный формат ключа зашифрованного хранилища", e);
        }

        if (candidate.Length != 32)
        {
            CryptographicOperations.ZeroMemory(candidate);
            throw new SecretStoreException("Неверная длина ключа зашифрованного хранилища");
        }

        // Только успешное открытие сохраняется в памяти. После «Отказать» следующая явная попытка снова разрешена.
        _key = candidate;
        return candidate;
    }

    private void Cleanup(string key)
    {
        if (file.NeedsLegacyCleanup(key))
        {
            // Метка записана той же транзакцией, что шифр. После отказа или падения между шагами очистка
            // повторится при следующем обращении; более новая зашифрованная копия старой не заменяется.
            legacy.Delete(key);
            file.FinishLegacyCleanup(key);
        }
    }

    private static byte[] AssociatedData(string key) => Encoding.UTF8.GetBytes("PrivateGateway:PGS1:" + key);
}
