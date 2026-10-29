using Google.Protobuf;
using PrivateGateway.Client.Core.Servers;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Contracts;

public static class SubscriptionContractMapping
{
    public static V1.SubscriptionDownloadReply ToContract(this SubscriptionDownload result) => new()
    {
        Content = ByteString.CopyFrom(result.Content ?? []),
        Title = result.Title ?? string.Empty,
        Failure = result.Failure?.ToString() ?? string.Empty,
        StatusCode = result.StatusCode ?? 0,
    };

    public static SubscriptionDownload ToCore(this V1.SubscriptionDownloadReply result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Content.Length > SubscriptionDownload.MaxBytes)
        {
            return SubscriptionDownload.Failed(DownloadFailure.TooLarge);
        }

        if (result.Failure.Length > 0)
        {
            return SubscriptionDownload.Failed(Enum.TryParse<DownloadFailure>(result.Failure, out var failure) && Enum.IsDefined(failure)
                ? failure : DownloadFailure.Network, result.StatusCode is > 0 ? result.StatusCode : null);
        }

        return new SubscriptionDownload(result.Content.ToByteArray(), result.Title.Length == 0 ? null : result.Title);
    }
}
