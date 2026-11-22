using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.Tests.UI;

public sealed class LocalSiteFilterPresentationTests : WindowHarness
{
    [AvaloniaFact]
    public async Task Local_filter_keeps_ip_visible_uses_partial_tone_and_requires_pin_to_stop()
    {
        ScriptedService? service = null;
        var (window, shell) = Open(gateway: sp => service = new ScriptedService(Time,
            () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed));
        Service<SettingsService>().Update(settings => settings with { BlockPin = BlockPin.Create("1234") });
        service!.Push(status => status.MoveTo(ConnectionState.Connecting, Time.GetUtcNow()).MoveTo(ConnectionState.Connected, Time.GetUtcNow()) with
        {
            LocalSiteFilter = true,
            Exit = new ExitInfo("192.0.2.10", null, null, null, "192.0.2.10"),
            Protection = new ProtectionChecks(CheckState.Warning, CheckState.Warning, CheckState.Ok, CheckState.Off, Time.GetUtcNow()),
        });
        Dispatcher.UIThread.RunJobs();
        var main = shell.CurrentPage.ShouldBeOfType<MainPageViewModel>();
        main.Tone.ShouldBe(Tone.Partial);
        main.StatusTitle.ShouldBe("Блок сайтов работает");
        main.WebIp.ShouldBe("192.0.2.10");
        main.RealIpState.ShouldBe("виден сайтам");
        main.ServerTitle.ShouldBe("Блок сайтов без VPN-сервера");
        main.Chips[0].Title.ShouldBe("Блок сайтов");
        main.Chips[0].Value.ShouldBe("IP виден · напрямую");
        shell.Protection.Status.HasExitIpLeak.ShouldBeFalse();
        Snapshot(window, "local-site-filter-main");

        var stop = shell.Protection.ToggleCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var prompt = shell.Overlay.Sheet.ShouldBeOfType<BlockPinPromptViewModel>();
        prompt.Pin = "0000";
        prompt.ConfirmCommand.Execute(null);
        service.Status.State.ShouldBe(ConnectionState.Connected);
        prompt.KeepCommand.Execute(null);
        await stop;
        service.Status.LocalSiteFilter.ShouldBeTrue();

        stop = shell.Protection.ToggleCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        prompt = shell.Overlay.Sheet.ShouldBeOfType<BlockPinPromptViewModel>();
        prompt.Pin = "1234";
        prompt.ConfirmCommand.Execute(null);
        await stop;
        service.Status.State.ShouldBe(ConnectionState.Off);
        service.Status.LocalSiteFilter.ShouldBeFalse();
    }

    [AvaloniaTheory]
    [InlineData(360)]
    [InlineData(440)]
    public void Offline_footer_wraps_explanation_before_the_network_button(int width)
    {
        ScriptedService? service = null;
        var (window, _) = Open(gateway: sp => service = new ScriptedService(Time,
            () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed));
        window.MinWidth = 0;
        window.Width = width;
        window.Height = 1100;
        service!.Push(status => status.MoveTo(ConnectionState.Connecting, Time.GetUtcNow()).MoveTo(ConnectionState.Connected, Time.GetUtcNow())
            .MoveTo(ConnectionState.Reconnecting, Time.GetUtcNow()) with
        { NetworkUnavailable = true });
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        var explanation = Find<TextBlock>(window, "CheckedText");
        var button = Find<Button>(window, "CheckAction");
        explanation.TextWrapping.ShouldBe(TextWrapping.Wrap);
        explanation.TextLayout.TextLines.Count.ShouldBeGreaterThan(1, "узкий экран переносит длинное пояснение");
        var textEnd = explanation.TranslatePoint(new Point(explanation.Bounds.Width, 0), window)!.Value.X;
        var buttonStart = button.TranslatePoint(default, window)!.Value.X;
        textEnd.ShouldBeLessThanOrEqualTo(buttonStart - 5, "между пояснением и кнопкой остается отступ");
        Snapshot(window, "offline-footer-" + width);
    }

    [AvaloniaFact]
    public void Pending_exit_ip_has_explicit_progress_and_replaces_it_without_manual_refresh()
    {
        ScriptedService? service = null;
        var (_, shell) = Open(gateway: sp => service = new ScriptedService(Time,
            () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed));
        service!.Push(status => status.MoveTo(ConnectionState.Connecting, Time.GetUtcNow()).MoveTo(ConnectionState.Connected, Time.GetUtcNow())
            with
        { Exit = ExitInfo.Unknown });
        Dispatcher.UIThread.RunJobs();
        var main = shell.CurrentPage.ShouldBeOfType<MainPageViewModel>();
        main.WebIpMeta.ShouldBe("Определяем внешний IP…");
        main.CanCopyIp.ShouldBeFalse();
        Time.Advance(TimeSpan.FromSeconds(3));
        service.Push(status => status with { Exit = new ExitInfo("192.0.2.20", null, null, null, "198.51.100.10") });
        Dispatcher.UIThread.RunJobs();
        main.WebIp.ShouldBe("192.0.2.20");
        main.CanCopyIp.ShouldBeTrue();
        main.WebIpMeta.ShouldNotBe("Определяем внешний IP…");
    }

    [AvaloniaFact]
    public void Disabled_automatic_checks_do_not_promise_a_pending_ip_measurement()
    {
        ScriptedService? service = null;
        var (_, shell) = Open(gateway: sp => service = new ScriptedService(Time,
            () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed));
        Service<SettingsService>().Update(settings => settings with
        {
            Protection = settings.Protection with { CheckLeaksAfterConnect = false },
        });
        service!.Push(status => status.MoveTo(ConnectionState.Connecting, Time.GetUtcNow()).MoveTo(ConnectionState.Connected, Time.GetUtcNow())
            with
        { Exit = ExitInfo.Unknown });
        Dispatcher.UIThread.RunJobs();
        var main = shell.CurrentPage.ShouldBeOfType<MainPageViewModel>();
        main.WebIpMeta.ShouldBe("Внешний IP не измерен · нажмите «Проверить»");
        main.CanCopyIp.ShouldBeFalse();
        Time.Advance(TimeSpan.FromMinutes(1));
        main.WebIpMeta.ShouldNotBe("Определяем внешний IP…");
        main.HasCheckAction.ShouldBeTrue("внешний IP можно запросить явной проверкой");
    }

    [AvaloniaFact]
    public void Late_history_from_the_previous_account_is_not_shown_after_sign_out_and_sign_in()
    {
        var history = new TaskCompletionSource<IReadOnlyList<BlockedSite>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (_, shell) = Open(gateway: sp => new ScriptedService(Time,
            () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed)
        { BlockedSitesReply = history });
        shell.NavigateCommand.Execute(Pages.Routing);
        Dispatcher.UIThread.RunJobs();
        var page = shell.CurrentPage.ShouldBeOfType<RoutingPageViewModel>();
        page.IsLoadingBlocked.ShouldBeTrue();
        Service<SettingsService>().Update(settings => settings with { AccountSignedOut = true });
        Service<SettingsService>().Update(settings => settings with { AccountSignedOut = false });
        history.SetResult([new BlockedSite("private.example.com", 1, Time.GetUtcNow())]);
        WaitFor(() => !page.IsLoadingBlocked);
        page.BlockedToday.ShouldBeEmpty("старый ответ принадлежит предыдущему входу, даже если новый уже завершен");
    }

    [AvaloniaFact]
    public async Task A_blocked_history_allow_requires_pin_and_opens_only_the_exact_name()
    {
        var history = new TaskCompletionSource<IReadOnlyList<BlockedSite>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        history.SetResult([new BlockedSite("part.example.com", 3, Time.GetUtcNow()), new BlockedSite("203.0.113.10", 1, Time.GetUtcNow())]);
        var (window, shell) = Open(gateway: sp => new ScriptedService(Time,
            () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed)
        { BlockedSitesReply = history });
        var block = new DomainRule("example.com", RuleAction.Block);
        Service<SettingsService>().Update(settings => settings with
        {
            BlockPin = BlockPin.Create("1234"),
            Routing = RoutingSettings.Default with { Rules = [block] },
        });
        shell.NavigateCommand.Execute(Pages.Routing);
        Dispatcher.UIThread.RunJobs();
        var page = shell.CurrentPage.ShouldBeOfType<RoutingPageViewModel>();
        var site = page.BlockedToday.Single(row => row.Host == "part.example.com");
        site.CanAllow.ShouldBeTrue();
        page.BlockedToday.Single(row => row.Host == "203.0.113.10").CanAllow.ShouldBeFalse("общий IP не раскрывается одним нажатием");
        Snapshot(window, "blocked-history-routing");

        var allow = site.AllowCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var prompt = shell.Overlay.Sheet.ShouldBeOfType<BlockPinPromptViewModel>();
        prompt.Pin = "0000";
        prompt.ConfirmCommand.Execute(null);
        Service<SettingsService>().Current.Routing.Rules.ShouldBe([block]);
        prompt.KeepCommand.Execute(null);
        await allow;

        allow = site.AllowCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        prompt = shell.Overlay.Sheet.ShouldBeOfType<BlockPinPromptViewModel>();
        prompt.Pin = "1234";
        prompt.ConfirmCommand.Execute(null);
        await allow;
        Service<SettingsService>().Current.Routing.Rules.ShouldBe([block, new DomainRule("part.example.com", RuleAction.Proxy, Exact: true)]);
    }
}
