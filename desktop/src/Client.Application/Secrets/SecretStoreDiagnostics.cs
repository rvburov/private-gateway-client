namespace PrivateGateway.Client.Application.Secrets;

/// <summary>Только количества и состояние процесса: без имен записей, путей, адресов и содержимого секретов.</summary>
public sealed record SecretStoreDiagnostics(string Kind, int EncryptedEntries, int PendingLegacyCleanup, int DeletedEntries,
    bool KeyUnlocked);
