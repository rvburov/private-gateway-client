using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Secrets;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.Tests.UI;

public sealed class SecretAccessUiTests : WindowHarness
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Denying_reconnection_keeps_the_active_server_selection_and_mode(bool fromProxy)
    {
        var settings = AppSettings.Default;
        var gateway = new ScriptedService(Time, () => settings, ServiceKind.Installed);
        var (window, shell) = Open(gateway: _ => gateway, demo: false);
        var store = Service<IServerStore>();
        store.Add([LinkParser.Parse("trojan://fake-a@198.51.100.1:443#A").Server!,
            LinkParser.Parse("trojan://fake-b@198.51.100.2:443#B").Server!], null);
        var original = store.Selected!.Id;
        var next = store.Servers.Single(s => s.Id != original).Id;
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        if (fromProxy)
        {
            settings = settings with { Mode = ConnectionMode.Proxy };
            Service<SettingsService>().Update(s => s with { Mode = ConnectionMode.Proxy });
        }
        var mode = Service<SettingsService>().Current.Mode;
        Secrets.ReadFailure = new SecretStoreException("Keychain access denied");
        var controller = Service<ConnectionController>();

        await Should.ThrowAsync<SecretStoreException>(async () =>
        {
            if (fromProxy)
                await controller.ConnectToAsync(next, TestContext.Current.CancellationToken);
            else
                await controller.SwitchServerAsync(next, TestContext.Current.CancellationToken);
        });

        window.IsVisible.ShouldBeTrue();
        store.Selected!.Id.ShouldBe(original);
        Service<SettingsService>().Current.Mode.ShouldBe(mode);
        gateway.Status.ServerId.ShouldBe(original);
        gateway.Status.State.ShouldBe(ConnectionState.Connected);
        Secrets.ReadFailure = null;
        await controller.ConnectToAsync(next, TestContext.Current.CancellationToken);
        gateway.Status.ServerId.ShouldBe(next);
        Service<SettingsService>().Current.Mode.ShouldBe(ConnectionMode.Vpn);
    }

    [AvaloniaFact]
    public void Denying_share_keeps_the_card_open_and_explicit_retry_can_show_the_qr()
    {
        var (window, shell) = Open(demo: false);
        var store = Service<IServerStore>();
        store.Add([LinkParser.Parse("trojan://fake-secret@198.51.100.1:443#A").Server!], null);
        shell.NavigateCommand.Execute(Pages.Servers);
        Dispatcher.UIThread.RunJobs();
        var page = shell.CurrentPage.ShouldBeOfType<ServersPageViewModel>();
        page.OpenCardCommand.Execute(store.Servers.Single().Id);
        var card = shell.Overlay.Sheet.ShouldBeOfType<ServerCardViewModel>();
        Secrets.ReadFailure = new SecretStoreException("Keychain access denied");

        card.ToggleShareCommand.Execute(null);

        window.IsVisible.ShouldBeTrue();
        card.IsSharing.ShouldBeFalse();
        card.QrModules.ShouldBeNull();
        shell.Toast.Text.ShouldBe(Loc.Instance["secrets.operation.failed"]);
        Secrets.ReadFailure = null;
        card.ToggleShareCommand.Execute(null);
        card.IsSharing.ShouldBeTrue();
        card.QrModules.ShouldNotBeNull();
    }

    [AvaloniaFact]
    public async Task Denying_import_preview_preserves_existing_servers_and_can_be_retried()
    {
        var (window, shell) = Open(demo: false);
        var store = Service<IServerStore>();
        const string link = "trojan://fake-secret@198.51.100.1:443#A";
        store.Add([LinkParser.Parse(link).Server!], null);
        shell.NavigateCommand.Execute(Pages.Add);
        Dispatcher.UIThread.RunJobs();
        var page = shell.CurrentPage.ShouldBeOfType<ImportPageViewModel>();
        page.OpenLinkCommand.Execute(null);
        page.LinkText = link;
        Secrets.ReadFailure = new SecretStoreException("Keychain access denied");

        await page.CheckLinkCommand.ExecuteAsync(null);

        window.IsVisible.ShouldBeTrue();
        page.CanAdd.ShouldBeFalse();
        page.Message.ShouldBe(Loc.Instance["secrets.operation.failed"]);
        store.Servers.ShouldHaveSingleItem();
        Secrets.ReadFailure = null;
        await page.CheckLinkCommand.ExecuteAsync(null);
        page.Preview.ShouldNotBeNull().Duplicates.ShouldBe(1);
    }

    [AvaloniaFact]
    public async Task Denying_connect_does_not_start_the_gateway_and_explicit_retry_works()
    {
        var gateway = new ScriptedService(Time, () => AppSettings.Default, ServiceKind.Installed);
        var (window, shell) = Open(gateway: _ => gateway, demo: false);
        var store = Service<IServerStore>();
        store.Add([LinkParser.Parse("trojan://fake-secret@198.51.100.1:443#A").Server!], null);
        Secrets.ReadFailure = new SecretStoreException("Keychain access denied");

        await shell.Protection.ToggleCommand.ExecuteAsync(null);

        window.IsVisible.ShouldBeTrue();
        gateway.Status.State.ShouldBe(ConnectionState.Off);
        shell.Toast.Text.ShouldBe(Loc.Instance["secrets.operation.failed"]);
        Secrets.ReadFailure = null;
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        gateway.Status.State.ShouldBe(ConnectionState.Connected);
    }

    [AvaloniaFact]
    public async Task Denying_access_when_refreshing_servers_does_not_close_the_window_and_allows_retry()
    {
        var gateway = new ScriptedService(Time, () => AppSettings.Default, ServiceKind.Installed)
        {
            LatencyError = new InvalidOperationException("Keychain access denied"),
        };
        var (window, shell) = Open(gateway: _ => gateway, demo: false);
        var store = Service<IServerStore>();
        store.Add([LinkParser.Parse("trojan://fake-secret@198.51.100.1:443#A").Server!], null);
        shell.NavigateCommand.Execute(Pages.Servers);
        Dispatcher.UIThread.RunJobs();
        var page = shell.CurrentPage.ShouldBeOfType<ServersPageViewModel>();

        await page.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        window.IsVisible.ShouldBeTrue();
        page.IsRefreshing.ShouldBeFalse();
        shell.Toast.Text.ShouldBe(Loc.Instance["secrets.operation.failed"]);
        store.Servers.ShouldHaveSingleItem().Name.ShouldBe("A");
        store.Servers.ShouldAllBe(s => !s.IsMeasuringLatency);

        gateway.LatencyError = null;
        await page.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        page.IsRefreshing.ShouldBeFalse();
        shell.Toast.Text.ShouldBe(Loc.Instance["server.refreshed.noPing"]);
    }
}
