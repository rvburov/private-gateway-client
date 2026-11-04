using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.UI.Controls;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>Выбор DNS, снимок политики и ограниченный сетевой замер показываются как разные факты.</summary>
public sealed class DnsClarityTests : WindowHarness
{
    [AvaloniaTheory]
    [InlineData("ru", 380, "По умолчанию: Cloudflare")]
    [InlineData("ru", 440, "По умолчанию: Cloudflare")]
    [InlineData("en", 380, "Default: Cloudflare")]
    [InlineData("en", 440, "Default: Cloudflare")]
    public void Default_choice_preserves_the_stored_value_and_fits_the_small_window(string language, int width, string label)
    {
        var (window, shell) = Open();
        Loc.Instance.SetLanguage(language);
        window.Width = width;
        var page = Navigate(shell);
        var selected = Find<TextBlock>(window, "DnsChoiceText");
        selected.BringIntoView();
        Dispatcher.UIThread.RunJobs();

        selected.Text.ShouldBe(label);
        page.DnsOptions[0].Label.ShouldBe(label);
        Service<SettingsService>().Current.Protection.Dns.ShouldBe(DnsChoice.Profile);
        ((int)DnsChoice.Profile).ShouldBe(0, "переименован только выбор, старое сохраненное значение остается совместимым");

        var fullText = new FormattedText(label, Loc.Instance.Culture, FlowDirection.LeftToRight,
            new Typeface(selected.FontFamily, selected.FontStyle, selected.FontWeight, selected.FontStretch), selected.FontSize, Brushes.Black);
        selected.Bounds.Width.ShouldBeGreaterThan(0);
        fullText.WidthIncludingTrailingWhitespace.ShouldBeLessThanOrEqualTo(selected.Bounds.Width + 0.5,
            "полное имя DNS видно без многоточия на узком окне");

        page.DnsOptions[2].ChooseCommand.Execute(null);
        page.DnsOptions[0].ChooseCommand.Execute(null);
        Service<SettingsService>().Current.Protection.Dns.ShouldBe(DnsChoice.Profile);
        page.DnsOptions[0].IsCurrent.ShouldBeTrue();
        page.DnsOptions[3].ChooseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var sheet = shell.Overlay.Sheet.ShouldBeOfType<DnsAddressViewModel>();
        sheet.Note.ShouldContain(language == "ru" ? "Публичный DNS в режиме VPN" : "In VPN mode, public DNS");
        sheet.Note.ShouldContain(language == "ru" ? "разрешенный прямой путь в локальную сеть" : "allowed direct path to the local network");
        Find<TextBlock>(window, "DnsNote").Text.ShouldBe(sheet.Note);
    }

    [AvaloniaFact]
    public void Selected_mode_and_dns_do_not_rewrite_the_service_snapshot()
    {
        ScriptedService? service = null;
        var (window, shell) = Open(gateway: sp => service = new ScriptedService(Time,
            () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed));
        var page = Navigate(shell);
        service!.Push(s => s with
        {
            State = ConnectionState.Connected,
            Mode = ConnectionMode.Proxy,
            Protection = ProtectionChecks.For(ConnectionState.Connected, ConnectionMode.Proxy),
        });
        Dispatcher.UIThread.RunJobs();

        page.DnsTitle.ShouldBe("DNS в туннеле", "настройки режима VPN остаются выбранными отдельно от снимка службы");
        page.DnsModeText.ShouldContain("ядро VPN");
        page.DnsPolicyText.ShouldContain("Только прокси");
        page.DnsPolicyText.ShouldContain("не перехватывает DNS всей системы");

        Service<SettingsService>().Update(s => s with { Protection = s.Protection with { Dns = DnsChoice.Quad9Doh, BlockOutsideDns = true } });
        Dispatcher.UIThread.RunJobs();
        page.DnsText.ShouldBe("DoH: Quad9");
        page.DnsPolicyText.Contains("Только прокси", StringComparison.Ordinal)
            .ShouldBeTrue("новый выбор не подтверждает применение в текущей сессии");

        page.DnsOptions[3].ChooseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var vpnSheet = shell.Overlay.Sheet.ShouldBeOfType<DnsAddressViewModel>();
        vpnSheet.Note.Contains("Публичный DNS в режиме VPN", StringComparison.Ordinal)
            .ShouldBeTrue("подсказка объясняет выбранный режим, не устаревшую сессию");
        Find<TextBlock>(window, "DnsNote").Text.ShouldBe(vpnSheet.Note);
        vpnSheet.CancelCommand.Execute(null);
        Service<SettingsService>().Update(s => s with { Mode = ConnectionMode.Proxy });
        page.DnsOptions[3].ChooseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var proxySheet = shell.Overlay.Sheet.ShouldBeOfType<DnsAddressViewModel>();
        proxySheet.Note.ShouldContain("ядро прокси");
        proxySheet.Note.ShouldContain("DNS всей системы не перехватывается");
        Find<TextBlock>(window, "DnsNote").Text.ShouldBe(proxySheet.Note);
    }

    [AvaloniaTheory]
    [InlineData(ConnectionState.Off, false, null, "Подключение выключено")]
    [InlineData(ConnectionState.Error, false, ErrorCodes.CoreFailed, "состоянии ошибки")]
    [InlineData(ConnectionState.Connected, true, null, "защита на паузе")]
    [InlineData(ConnectionState.Error, false, ErrorCodes.ServiceNotRunning, "действующее состояние DNS-защиты неизвестно")]
    public void A_selected_blocking_switch_does_not_make_missing_protection_active(ConnectionState state, bool paused, string? code, string expected)
    {
        ScriptedService? service = null;
        var (_, shell) = Open(gateway: sp => service = new ScriptedService(Time,
            () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed));
        var page = Navigate(shell);
        page.BlockOutsideDns.ShouldBeTrue();
        service!.Push(s => s with
        {
            State = state,
            ErrorCode = code,
            CaptivePauseUntil = paused ? Time.GetUtcNow().AddMinutes(5) : null,
            // Старый положительный флаг не сильнее состояния Off/Error/паузы и отсутствия службы.
            Protection = ProtectionChecks.For(ConnectionState.Connected),
        });
        Dispatcher.UIThread.RunJobs();

        page.DnsPolicyText.ShouldContain(expected);
        page.DnsPolicyText.ShouldNotContain("действует по данным службы");
    }

    [AvaloniaTheory]
    [InlineData("dns_no_answer", CheckState.Warning, "Не подтверждено")]
    [InlineData("dns_unavailable", CheckState.Warning, "Не измерено")]
    [InlineData("timeout", CheckState.Warning, "Нет результата")]
    [InlineData(null, CheckState.Ok, "Снимок службы")]
    public void Unconfirmed_or_legacy_dns_probe_is_neutral_without_changing_policy(string? reason, CheckState state, string badge)
    {
        ScriptedService? service = null;
        var (window, shell) = Open(gateway: sp => service = new ScriptedService(Time,
            () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed)
        {
            Checks = [new DiagnosticResult(DiagnosticCheck.Dns, state, reason)],
        });
        service!.Push(s => s with
        {
            State = ConnectionState.Connected,
            Mode = ConnectionMode.Vpn,
            Protection = ProtectionChecks.For(ConnectionState.Connected),
        });
        Dispatcher.UIThread.RunJobs();
        var main = shell.CurrentPage.ShouldBeOfType<MainPageViewModel>();
        (main.Chips[1].Title, main.Chips[1].Value).ShouldBe(("Правила DNS", "применены"));
        main.Chips[1].Text.ShouldContain("не проверка всех DNS-запросов браузера");

        shell.NavigateCommand.Execute(Pages.Check);
        var page = shell.CurrentPage.ShouldBeOfType<CheckPageViewModel>();
        WaitFor(() => !page.IsRunning);
        var row = page.Rows.Single(r => r.Check == DiagnosticCheck.Dns);
        row.State.ShouldBe(state, "представление не переписывает исходный результат службы");
        row.Reason.ShouldBe(reason);
        row.ChipText.ShouldBe(badge);
        row.IsInconclusive.ShouldBeTrue();
        row.DisplayState.ShouldBe(CheckState.Waiting);
        row.Icon.ShouldBe("Icon.Chip.Dash");
        row.Detail.ShouldNotContain("утечек нет");
        row.Detail.ShouldNotContain("Проверяется");
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var rendered = window.GetVisualDescendants().OfType<Border>()
            .Single(border => border.Classes.Contains("checkRow") && ReferenceEquals(border.DataContext, row));
        var dot = rendered.GetVisualDescendants().OfType<Border>().Single(border => border.Width == 22 && border.Height == 22);
        var chip = rendered.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == badge);
        ((dot.Background as ISolidColorBrush)?.Color).ShouldBe(ResourceColor("PgBrush.Accent.Base"));
        ((chip.Foreground as ISolidColorBrush)?.Color).ShouldBe(ResourceColor("PgBrush.Accent.Text"));
        (((chip.GetVisualParent() as Border)?.Background as ISolidColorBrush)?.Color).ShouldBe(ResourceColor("PgBrush.Accent.Tint"));
        rendered.GetVisualDescendants().OfType<Icon>().Single().Classes.ShouldNotContain("spin");
        service.Status.Protection.Dns.ShouldBe(CheckState.Ok);
        main.Chips[1].Value.ShouldBe("применены", "неизвестная проба не отменяет снимок действующей политики");
    }

    [AvaloniaTheory]
    [InlineData(ConnectionState.Off, CheckState.Off, "не применены")]
    [InlineData(ConnectionState.Reconnecting, CheckState.Waiting, "ожидаются")]
    public void Inactive_proxy_does_not_show_connected_proxy_dns(ConnectionState state, CheckState policy, string value)
    {
        ScriptedService? service = null;
        var (_, shell) = Open(gateway: sp => service = new ScriptedService(Time,
            () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed));
        var main = shell.CurrentPage.ShouldBeOfType<MainPageViewModel>();
        service!.Push(s => s with
        {
            State = state,
            Mode = ConnectionMode.Proxy,
            Protection = s.Protection with { Dns = policy },
        });
        Dispatcher.UIThread.RunJobs();

        (main.Chips[1].Title, main.Chips[1].Value).ShouldBe(("Правила DNS", value));
        main.Chips[1].Value.ShouldNotBe("только прокси", "вариант относится только к подключенному прокси");
        var protection = Navigate(shell);
        protection.DnsPolicyText.ShouldNotContain("частичная");
        protection.DnsPolicyText.ShouldContain(state == ConnectionState.Off ? "Подключение выключено" : "ожидает подключения");
    }

    private static Color ResourceColor(string key) =>
        Avalonia.Application.Current!.TryGetResource(key, ThemeVariant.Light, out var value)
            ? ((ISolidColorBrush)value!).Color : throw new KeyNotFoundException(key);

    private static ProtectionPageViewModel Navigate(ShellViewModel shell)
    {
        shell.NavigateCommand.Execute(Pages.Protection);
        Dispatcher.UIThread.RunJobs();
        return shell.CurrentPage.ShouldBeOfType<ProtectionPageViewModel>();
    }
}
