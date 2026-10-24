using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Application.Import;

/// <summary>
/// Предпросмотр импорта (экран 3): что разобрано, сколько новых, дублей и ошибок. Ничего не
/// сохраняет — только считает.
/// </summary>
public sealed record ImportPreview(ImportBatch Batch, int Duplicates)
{
    public IReadOnlyList<ServerDefinition> Servers { get; } =
        [.. Batch.Lines.Where(l => l.Result.IsOk).Select(l => l.Result.Server!)];

    public int Found => Batch.Found;

    public int New => Servers.Count - Duplicates;

    public int Failed => Batch.Failed;

    /// <summary>Протоколы среди разобранных серверов в порядке фильтра списка серверов.</summary>
    public IReadOnlyList<ProxyProtocol> Protocols =>
        [.. ServerList.ProtocolOrder.Where(p => Servers.Any(s => s.Protocol == p))];

    /// <summary>Строки с ошибками: номер и код из <see cref="ImportErrors"/>.</summary>
    public IReadOnlyList<ImportLine> Errors => [.. Batch.Lines.Where(l => !l.Result.IsOk)];

    public static ImportPreview Of(string text, IServerStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        var batch = ImportParser.Parse(text);
        var servers = batch.Lines.Where(l => l.Result.IsOk).Select(l => l.Result.Server!).ToList();
        return new ImportPreview(batch, store.CountDuplicates(servers));
    }
}

/// <summary>
/// Загрузка подписки по адресу. Единственная сеть процесса интерфейса — только здесь, в
/// Infrastructure. Адрес подписки — секрет: в журнал и в ошибки он не попадает.
/// </summary>
public interface ISubscriptionDownloader
{
    /// <exception cref="SubscriptionDownloadException">Не загрузилось: сеть, код ответа, размер.</exception>
    Task<string> DownloadAsync(Uri url, CancellationToken cancellationToken = default);
}

/// <summary>Почему подписка не загрузилась; текст для пользователя — в интерфейсе, на его языке.</summary>
public enum DownloadFailure
{
    Network,
    NameResolution,
    Connection,
    Tls,
    Timeout,

    /// <summary>Сервер ответил не 2xx — код в <see cref="SubscriptionDownloadException.StatusCode"/>.</summary>
    Status,
    TooLarge,
}

/// <summary>Подписка не загрузилась. Ни причина, ни текст исключения не содержат адреса подписки.</summary>
public sealed class SubscriptionDownloadException : Exception
{
    public SubscriptionDownloadException()
    {
    }

    public SubscriptionDownloadException(string message)
        : base(message)
    {
    }

    public SubscriptionDownloadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SubscriptionDownloadException(DownloadFailure failure, int? statusCode = null, Exception? innerException = null)
        : base(statusCode is { } code ? $"{failure} {code}" : failure.ToString(), innerException)
    {
        Failure = failure;
        StatusCode = statusCode;
    }

    public DownloadFailure Failure { get; }

    public int? StatusCode { get; }
}
