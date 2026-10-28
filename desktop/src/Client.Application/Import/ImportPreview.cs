using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Application.Import;

/// <summary>
/// Предпросмотр импорта (экран 3): что разобрано, сколько новых, дублей и ошибок, у скольких
/// сохраненных обновится защита (<paramref name="Renewals"/>, решение 484). Ничего не сохраняет — только считает.
/// </summary>
public sealed record ImportPreview(ImportBatch Batch, int Duplicates, int Renewals = 0)
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
        return new ImportPreview(batch, store.CountDuplicates(servers), store.CountRenewals(servers));
    }
}

/// <summary>
/// Загрузка подписки по адресу. Единственная сеть процесса интерфейса — только здесь, в
/// Infrastructure. Адрес подписки — секрет: в журнал и в ошибки он не попадает.
/// </summary>
public interface ISubscriptionDownloader
{
    /// <exception cref="SubscriptionDownloadException">Не загрузилось: сеть, код ответа, размер.</exception>
    Task<SubscriptionContent> DownloadAsync(Uri url, CancellationToken cancellationToken = default);
}

/// <summary>
/// Ответ подписки: текст и название из заголовка Profile-Title (у PRIVATE GATEWAY — имя устройства,
/// так же его читают v2rayN, Clash и Hiddify).
/// </summary>
public sealed record SubscriptionContent(string Text, string? Title)
{
    private const int MaxTitle = 100;

    /// <summary>
    /// Название из Profile-Title: «base64:…» в UTF-8 или обычный текст ASCII (иначе заголовок HTTP
    /// искажает буквы). Пустое, битое и с управляющими знаками — null; длинное обрезается.
    /// </summary>
    public static string? TitleFrom(string? header)
    {
        var value = header?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
        {
            value = LinkParser.DecodeBase64(value["base64:".Length..])?.Trim();
        }
        else if (value.Any(c => c > '~'))
        {
            return null;
        }

        if (string.IsNullOrEmpty(value) || value.Any(char.IsControl))
        {
            return null;
        }

        return value.Length > MaxTitle ? value[..MaxTitle].TrimEnd() : value;
    }
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
