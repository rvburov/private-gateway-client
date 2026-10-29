namespace PrivateGateway.Client.Core.Servers;

/// <summary>Ограниченный ответ подписки через защищенное ядро; адрес и текст сетевой ошибки не возвращаются.</summary>
public sealed record SubscriptionDownload(byte[]? Content, string? Title = null, DownloadFailure? Failure = null, int? StatusCode = null)
{
    public const int MaxBytes = 5 * 1024 * 1024;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static SubscriptionDownload Failed(DownloadFailure failure, int? statusCode = null) => new(null, Failure: failure, StatusCode: statusCode);

    /// <summary>WireGuard сам разрешает имя назначения: локальный DNS ядра нарушил бы обещание загрузки только через VPN.</summary>
    public static SubscriptionDownload? RefuseUnsafeDns(ProxyProtocol protocol, bool dnsMayBypassServer) =>
        protocol == ProxyProtocol.WireGuard && dnsMayBypassServer ? Failed(DownloadFailure.UnsafeDns) : null;
}
