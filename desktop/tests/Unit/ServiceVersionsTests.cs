using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Порядок версий (<see cref="SemVer"/>), совместимость окна и службы (<see cref="ServiceVersions"/>) и сведения службы из контракта.</summary>
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
        SemVer.Compare(left, right).ShouldBe(order, $"{left} и {right}");
        SemVer.Compare(right, left).ShouldBe(-order, "порядок в обе стороны");
    }

    [Theory]
    [InlineData("0.2.0", "1", ServiceMatch.Same)]
    [InlineData("0.1.0", "1", ServiceMatch.Older)]
    [InlineData("0.3.0-beta.1", "1", ServiceMatch.Newer)]
    [InlineData("0.2.0", "2", ServiceMatch.Incompatible)]
    public void Service_is_compared_with_the_window(string service, string api, ServiceMatch match) =>
        ServiceVersions.Compare("0.2.0", new ServiceDetails(service, api, []), "1").ShouldBe(match);

    /// <summary>
    /// Сборки из исходников все одной версии: установленную службу из прежних исходников окно узнает по коммиту. Неизвестный
    /// коммит с любой стороны — не повод звать обновлять; другая версия и другой контракт важнее коммита.
    /// </summary>
    [Theory]
    [InlineData("0.2.0", "1", "e7ddb17", "9982e44", ServiceMatch.OtherBuild)]
    [InlineData("0.2.0", "1", "e7ddb17", "e7ddb17", ServiceMatch.Same)]
    [InlineData("0.2.0", "1", "E7DDB17", "e7ddb17", ServiceMatch.Same)]
    [InlineData("0.2.0", "1", "", "e7ddb17", ServiceMatch.Same)]
    [InlineData("0.2.0", "1", "e7ddb17", "", ServiceMatch.Same)]
    [InlineData("0.1.0", "1", "e7ddb17", "9982e44", ServiceMatch.Older)]
    [InlineData("0.3.0", "1", "e7ddb17", "9982e44", ServiceMatch.Newer)]
    [InlineData("0.2.0", "2", "e7ddb17", "9982e44", ServiceMatch.Incompatible)]
    public void Service_of_the_same_version_from_other_sources_is_another_build(
        string service, string api, string serviceBuild, string windowBuild, ServiceMatch match) =>
        ServiceVersions.Compare("0.2.0", new ServiceDetails(service, api, [], serviceBuild), "1", windowBuild).ShouldBe(match);

    [Theory]
    [InlineData("0.1.0+e7ddb179071ada7aa96dad96897e87924a140b1d", "e7ddb179071ada7aa96dad96897e87924a140b1d")]
    [InlineData("1.2.3-beta.1+abc ", "abc")]
    [InlineData("0.1.0", "")]
    [InlineData(null, "")]
    public void Build_is_what_follows_the_plus(string? version, string build) => SemVer.Build(version).ShouldBe(build);

    [Theory]
    [InlineData("1.2.3", true)]
    [InlineData("0.4.0-beta.1", true)]
    [InlineData("1.2.3+git-abc", true)]
    [InlineData("v1.2.3", false)]
    [InlineData("1.2", false)]
    [InlineData("1.2.3 ", false)]
    [InlineData("1.2.3-", false)]
    [InlineData("../1.2.3", false)]
    public void Only_semver_is_a_version(string version, bool valid) => SemVer.IsValid(version).ShouldBe(valid);

    [Fact]
    public void Service_info_comes_from_the_contract_with_working_cores_only()
    {
        var info = new V1.ServiceInfo { ServiceVersion = "0.2.0", ServiceBuild = "e7ddb17", ApiVersion = "1" };
        info.Cores.Add(new V1.CoreInfo { Name = "xray", Version = "25.8.3", Available = true });
        info.Cores.Add(new V1.CoreInfo { Name = "sing-box", Version = string.Empty, Available = false });

        var details = info.ToCore();

        (details.Version, details.ApiVersion, details.Build).ShouldBe(("0.2.0", "1", "e7ddb17"));
        new V1.ServiceInfo { ServiceVersion = "0.2.0", ApiVersion = "1" }.ToCore().Build.ShouldBeEmpty("прежняя служба сборку не называет");
        details.Core("xray").ShouldBe(new CoreDetails("xray", "25.8.3", true));
        details.Core("sing-box").ShouldBeNull("ядро, которое не запускается, — как нет");
    }
}
