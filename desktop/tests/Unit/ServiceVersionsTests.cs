using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Совместимость окна и службы (<see cref="ServiceVersions"/>) и сведения службы из контракта.</summary>
public class ServiceVersionsTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("1.2.3+abc", "1.2.3+def", 0)]
    [InlineData("1.2.3+git-abc", "1.2.3", 0)]
    [InlineData("1.2.3", "1.2.4", -1)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("2.0.0", "10.0.0", -1)]
    [InlineData("1.2.3-beta.1", "1.2.3", -1)]
    [InlineData("1.2.3-beta.2", "1.2.3-beta.10", -1)]
    [InlineData("1.2.3-alpha", "1.2.3-beta", -1)]
    [InlineData("1.2.3-1", "1.2.3-alpha", -1)]
    [InlineData("1.2.3-beta", "1.2.3-beta.1", -1)]
    public void Versions_are_ordered_by_semver(string left, string right, int order)
    {
        ServiceVersions.CompareSemVer(left, right).ShouldBe(order, $"{left} и {right}");
        ServiceVersions.CompareSemVer(right, left).ShouldBe(-order, "порядок в обе стороны");
    }

    [Theory]
    [InlineData("0.2.0", "1", ServiceMatch.Same)]
    [InlineData("0.1.0", "1", ServiceMatch.Older)]
    [InlineData("0.3.0-beta.1", "1", ServiceMatch.Newer)]
    [InlineData("0.2.0", "2", ServiceMatch.Incompatible)]
    public void Service_is_compared_with_the_window(string service, string api, ServiceMatch match) =>
        ServiceVersions.Compare("0.2.0", new ServiceDetails(service, api, []), "1").ShouldBe(match);

    [Fact]
    public void Service_info_comes_from_the_contract_with_working_cores_only()
    {
        var info = new V1.ServiceInfo { ServiceVersion = "0.2.0", ApiVersion = "1" };
        info.Cores.Add(new V1.CoreInfo { Name = "xray", Version = "25.8.3", Available = true });
        info.Cores.Add(new V1.CoreInfo { Name = "sing-box", Version = string.Empty, Available = false });

        var details = info.ToCore();

        (details.Version, details.ApiVersion).ShouldBe(("0.2.0", "1"));
        details.Core("xray").ShouldBe(new CoreDetails("xray", "25.8.3", true));
        details.Core("sing-box").ShouldBeNull("ядро, которое не запускается, — как нет");
    }
}
