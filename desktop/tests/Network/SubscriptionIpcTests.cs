using Grpc.Core;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core.Servers;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

public sealed class SubscriptionIpcTests
{
    [Fact]
    public async Task A_subscription_is_refused_over_ipc_without_an_active_vpn_and_never_downloaded_directly()
    {
        using var site = new LocalSite("must-not-be-read");
        await using var service = await RunningService.StartAsync();
        var result = (await service.Client.DownloadSubscriptionAsync(new V1.SubscriptionDownloadRequest { Address = site.Url.AbsoluteUri },
            cancellationToken: TestContext.Current.CancellationToken)).ToCore();

        result.Failure.ShouldBe(DownloadFailure.Connection);
        result.Content.ShouldBeNull();
        site.Requests.ShouldBe(0);
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    public async Task The_subscription_rpc_accepts_only_http_addresses(string address)
    {
        await using var service = await RunningService.StartAsync();
        var error = await Should.ThrowAsync<RpcException>(async () => await service.Client.DownloadSubscriptionAsync(
            new V1.SubscriptionDownloadRequest { Address = address }, cancellationToken: TestContext.Current.CancellationToken));
        error.StatusCode.ShouldBe(StatusCode.InvalidArgument);
    }
}
