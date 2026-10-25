namespace PrivateGateway.Client.Core.Platform;

/// <summary>
/// Шифрование секретов службы хранилищем ОС: расшифровать может только служба этой машины (Windows — DPAPI
/// от имени SYSTEM, Linux — <c>systemd-creds</c> с ключом машины, доступным только root). Нужно, чтобы служба
/// сама подключалась при загрузке, до входа пользователя: ключи сервера у окна — в хранилище пользователя.
/// </summary>
public interface ISecretSealer
{
    /// <summary>Чем шифрует — для журнала.</summary>
    string Name { get; }

    /// <summary>Зашифровать; не вышло — <see cref="IOException"/>.</summary>
    byte[] Seal(byte[] plain);

    /// <summary>Расшифровать; чужое, испорченное или ключ машины сменился — null.</summary>
    byte[]? Unseal(byte[] sealedData);
}
