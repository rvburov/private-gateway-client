using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Infrastructure.Service;

namespace PrivateGateway.Client.Tests.Unit;

public sealed class MacServiceAccessTests
{
    [Theory]
    [InlineData(0, ServiceAccess.Installed)]
    [InlineData(1, ServiceAccess.Installed)]
    [InlineData(2, ServiceAccess.RequiresApproval)]
    [InlineData(3, ServiceAccess.Installed)]
    [InlineData(99, ServiceAccess.Unknown)]
    public void Only_requires_approval_proves_a_background_denial(int native, ServiceAccess expected) =>
        new MacServiceAccess(() => true, () => native, () => { }).Read().ShouldBe(expected);

    [Fact]
    public void Missing_plist_is_not_a_stale_denial_in_the_os_database() =>
        new MacServiceAccess(() => false, () => throw new InvalidOperationException(), () => { }).Read().ShouldBe(ServiceAccess.NotInstalled);

    [Fact]
    public void Failed_read_never_claims_that_the_user_denied_permission() =>
        new MacServiceAccess(() => true, () => throw new DllNotFoundException(), () => { }).Read().ShouldBe(ServiceAccess.Unknown);

    [Fact]
    public void Checking_does_not_open_settings_and_open_failure_is_reported()
    {
        var opened = 0;
        var access = new MacServiceAccess(() => true, () => 2, () => opened++);
        access.Read().ShouldBe(ServiceAccess.RequiresApproval);
        opened.ShouldBe(0);
        access.OpenSettings().ShouldBeTrue();
        opened.ShouldBe(1);
        new MacServiceAccess(() => true, () => 2, () => throw new EntryPointNotFoundException()).OpenSettings().ShouldBeFalse();
    }
}
