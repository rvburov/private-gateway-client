using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Infrastructure.Storage;
using PrivateGateway.Client.UI;
using PrivateGateway.Client.UI.Controls;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.Tests.UI;

public sealed class SettingsPageTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "pg-ui-" + Guid.NewGuid().ToString("N")));
    private readonly List<ServiceProvider> _providers = [];

    public void Dispose()
    {
        Loc.Instance.SetLanguage("ru");
        foreach (var provider in _providers)
        {
            provider.Dispose();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_paths.DataDirectory))
        {
            Directory.Delete(_paths.DataDirectory, recursive: true);
        }
    }

    [AvaloniaFact]
    public void Defaults_match_decisions_and_screen_matches_mockup()
    {
        var (window, page) = OpenSettings();

        page.IsVpnMode.ShouldBeTrue();
        page.KillSwitch.ShouldBeTrue();
        page.NotifyImportant.ShouldBeTrue();
        page.IsRussian.ShouldBeTrue();
        page.Advanced.Select(a => a.Title).ShouldContain("Защита и утечки");
        Snapshot(window, "settings");
    }

    [AvaloniaFact]
    public void Changes_are_saved_at_once_and_survive_a_restart()
    {
        var (window, page) = OpenSettings();

        page.KillSwitch = false;
        Dispatcher.UIThread.RunJobs();
        ((ShellViewModel)window.DataContext!).Overlay.Dialog!.SecondaryCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        page.ConnectOnLaunch = true;
        page.NotifyOff = true;

        var reopened = Build().GetRequiredService<SettingsService>().Current;
        reopened.KillSwitch.ShouldBeFalse();
        reopened.ConnectOnLaunch.ShouldBeTrue();
        reopened.Notifications.ShouldBe(Core.Settings.NotificationLevel.Off);
    }

    [AvaloniaFact]
    public void Turning_the_kill_switch_off_asks_first_and_keeps_it_by_default()
    {
        var (window, page) = OpenSettings();
        var overlay = ((ShellViewModel)window.DataContext!).Overlay;

        page.KillSwitch = false;
        Dispatcher.UIThread.RunJobs();

        var dialog = overlay.Dialog.ShouldNotBeNull();
        dialog.Title.ShouldBe("Выключить блок при обрыве?");
        dialog.PrimaryText.ShouldBe("Оставить включенным");
        dialog.IsSecondaryDanger.ShouldBeTrue();
        page.KillSwitch.ShouldBeTrue("до ответа ничего не меняется");
        window.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "KillSwitchToggle").IsChecked.ShouldBe(true);

        overlay.DismissCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        page.KillSwitch.ShouldBeTrue("Escape — безопасный вариант");
        Build().GetRequiredService<SettingsService>().Current.KillSwitch.ShouldBeTrue();
    }

    [AvaloniaFact]
    public void Switching_from_vpn_to_proxy_asks_first_and_keeps_vpn_by_default()
    {
        // Просьба пользователя 2026-11-01: случайное нажатие «Только прокси» не должно ослабить защиту.
        var (window, page) = OpenSettings();
        var overlay = ((ShellViewModel)window.DataContext!).Overlay;
        var proxy = window.GetVisualDescendants().OfType<ConfirmRadioButton>().Single(r => r.Name == "ProxyMode");

        Press(window, proxy);

        var dialog = overlay.Dialog.ShouldNotBeNull();
        (dialog.Title, dialog.PrimaryText, dialog.SecondaryText).ShouldBe(("Перейти в режим прокси?", "Оставить VPN", "Перейти на прокси"));
        dialog.IsSecondaryDanger.ShouldBeTrue();
        dialog.Text.ShouldContain("Блок при обрыве работать не будет");
        (page.IsVpnMode, page.IsProxyMode, proxy.IsChecked).ShouldBe((true, false, false), "до ответа режим прежний и переключатель стоит на VPN");
        Build().GetRequiredService<SettingsService>().Current.Mode.ShouldBe(ConnectionMode.Vpn);

        overlay.DismissCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        page.IsVpnMode.ShouldBeTrue("Escape и щелчок мимо — остаться на VPN");
        Build().GetRequiredService<SettingsService>().Current.Mode.ShouldBe(ConnectionMode.Vpn);

        Press(window, proxy);
        overlay.Dialog.ShouldNotBeNull().SecondaryCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        (page.IsProxyMode, proxy.IsChecked).ShouldBe((true, true));
        var vpn = window.GetVisualDescendants().OfType<RadioButton>().Single(b => b.Name == "VpnMode");
        Press(window, vpn);
        (page.IsProxyMode, vpn.IsChecked).ShouldBe((true, false), "до подтверждения прокси остается выбранным");
        overlay.Dialog.ShouldNotBeNull().PrimaryCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        page.IsProxyMode.ShouldBeTrue("отказ сохраняет режим прокси");

        Press(window, vpn);
        overlay.Dialog.ShouldNotBeNull().SecondaryCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        (page.IsVpnMode, vpn.IsChecked, overlay.Dialog).ShouldBe((true, true, null));
    }

    [AvaloniaFact]
    public void Proxy_mode_disables_kill_switch_and_explains_why()
    {
        var (window, page) = OpenSettings();

        page.ChooseProxyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        ((ShellViewModel)window.DataContext!).Overlay.Dialog.ShouldNotBeNull().SecondaryCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        page.CanKillSwitch.ShouldBeFalse();
        page.ModeNote.ShouldContain("Блок при обрыве недоступен");
        window.GetVisualDescendants().OfType<ToggleButton>().Single(t => t.Name == "KillSwitchToggle").IsEnabled.ShouldBeFalse();
        Build().GetRequiredService<SettingsService>().Current.Mode.ShouldBe(ConnectionMode.Proxy);
    }

    [AvaloniaFact]
    public void English_switches_the_interface_and_is_remembered()
    {
        var (window, page) = OpenSettings();

        page.IsEnglish = true;
        Dispatcher.UIThread.RunJobs();

        Loc.Instance.Language.ShouldBe("en");
        window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ShouldContain("Block internet if the VPN drops");
        Build().GetRequiredService<SettingsService>().Current.Language.ShouldBe("en");
        Snapshot(window, "settings-en");
    }

    [AvaloniaFact]
    public void Advanced_links_open_their_pages_and_keep_settings_active_in_island()
    {
        var (_, page) = OpenSettings();
        var shell = _providers[0].GetRequiredService<ShellViewModel>();

        page.Advanced.Single(a => a.Id == "routing").OpenCommand.Execute("routing");

        shell.CurrentKey.ShouldBe(Pages.Routing);
        shell.IsSettings.ShouldBeTrue();
        shell.CurrentPage.ShouldBeOfType<RoutingPageViewModel>();

        page.Advanced.Single(a => a.Id == "backup").OpenCommand.Execute("backup");
        shell.CurrentPage.ShouldBeOfType<BackupPageViewModel>();

        page.Advanced.Single(a => a.Id == "about").OpenCommand.Execute("about");
        shell.IsSettings.ShouldBeTrue();
        shell.CurrentPage.ShouldBeOfType<AboutPageViewModel>();
    }

    private (MainWindow Window, SettingsPageViewModel Page) OpenSettings()
    {
        var shell = Build().GetRequiredService<ShellViewModel>();
        var window = new MainWindow { DataContext = shell };
        window.Show();
        shell.NavigateCommand.Execute(Pages.Settings);
        Dispatcher.UIThread.RunJobs();
        return (window, (SettingsPageViewModel)shell.CurrentPage!);
    }

    private ServiceProvider Build()
    {
        var provider = App.BuildServices(_paths, demo: true);
        _providers.Add(provider);
        return provider;
    }

    private static void Snapshot(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(Repo.Snapshots, name + ".png"), PngBitmapEncoderOptions.Default);
    }

    /// <summary>Нажатие мышью, как у пользователя: сегмент — в видимой части страницы.</summary>
    private static void Press(Window window, Control control)
    {
        var scroll = control.FindAncestorOfType<ScrollViewer>()!;
        var top = control.TranslatePoint(default, (Visual)scroll.Content!)!.Value.Y;
        scroll.Offset = new Vector(0, Math.Max(0, top - 120));
        Dispatcher.UIThread.RunJobs();
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
