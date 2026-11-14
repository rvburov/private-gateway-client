namespace PrivateGateway.Client.Application.Secrets;

/// <summary>ОС отказала в доступе к секретам или не смогла их открыть. Это не отсутствие записи.</summary>
public sealed class SecretStoreException : InvalidOperationException
{
    public SecretStoreException(string message) : base(message) { }

    public SecretStoreException(string message, Exception innerException) : base(message, innerException) { }
}
