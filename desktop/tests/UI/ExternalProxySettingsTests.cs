using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Notifications;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>Внешний выход выбирается по режиму, сохраняется целиком и не заменяет выбранный VPN-профиль.</summary>
public sealed class ExternalProxySettingsTests : WindowHarness
{
    [AvaloniaTheory]
    [InlineData("ru", 380)]
    [InlineData("ru", 440)]
    [InlineData("en", 380)]
    [InlineData("en", 440)]
    public void Proxy_form_is_mode_specific_and_labels_fit_the_small_window(string language, int width)
    {
        var (window, shell) = Open();
        window.Width = width;
        Loc.Instance.SetLanguage(language);
        var page = Settings(shell);
        Find<Control>(window, "ExternalProxyForm").IsVisible.ShouldBeFalse();

        Service<SettingsService>().Update(s => s with { Mode = ConnectionMode.Proxy });
        Dispatcher.UIThread.RunJobs();
        Find<Control>(window, "ExternalProxyForm").IsVisible.ShouldBeTrue();
        Find<StackPanel>(window, "ExternalProxyCredentials").IsVisible.ShouldBeFalse();

        page.ExternalProxy.AuthenticationOptions[1].ChooseCommand.Execute(null);
        page.ExternalProxy.ProtocolOptions[2].ChooseCommand.Execute(null);
        Find<Button>(window, "ExternalProxyAuthentication").BringIntoView();
        Dispatcher.UIThread.RunJobs();
        Find<StackPanel>(window, "ExternalProxyCredentials").IsVisible.ShouldBeTrue();
        Find<TextBox>(window, "ExternalProxyPassword").PasswordChar.ShouldBe('●');

        var selector = Find<Button>(window, "ExternalProxyAuthentication");
        selector.Bounds.Width.ShouldBeGreaterThan(0);
        var text = new FormattedText(page.ExternalProxy.AuthenticationText, Loc.Instance.Culture, FlowDirection.LeftToRight,
            new Typeface(selector.FontFamily, selector.FontStyle, selector.FontWeight, selector.FontStretch), selector.FontSize, Brushes.Black);
        text.WidthIncludingTrailingWhitespace.ShouldBeLessThan(selector.Bounds.Width - 36,
            "имя авторизации остается целиком видимым на узком окне");
        Find<TextBox>(window, "ExternalProxyHost").Bounds.Width.ShouldBeLessThan(window.Width);
        page.ExternalProxy.IsUnencrypted.ShouldBeFalse("HTTPS шифрует соединение с прокси");
    }

    [AvaloniaFact]
    public async Task A_draft_does_not_reconnect_or_overwrite_either_modes_saved_settings()
    {
        var (window, shell) = Open();
        var settings = Service<SettingsService>();
        var catalog = Service<IServerCatalog>();
        var vpn = catalog.Selected.ShouldNotBeNull();
        var original = settings.Current;
        var page = Settings(shell);
        settings.Update(s => s with { Mode = ConnectionMode.Proxy });
        var editor = page.ExternalProxy;
        editor.Protocol = ExternalProxyProtocol.Https;
        editor.Host = "proxy.example.com";
        editor.Port = "8443";
        editor.Authentication = ProxyAuthentication.UserPassword;
        editor.Username = "proxy-test-user";
        editor.Password = "proxy-test-pass";

        settings.Current.ExternalProxy.ShouldBe(ExternalProxySettings.Default, "символы черновика не меняют живое подключение");
        shell.Protection.Status.State.ShouldBe(ConnectionState.Off);
        await editor.SaveCommand.ExecuteAsync(null);
        editor.HasError.ShouldBeFalse();
        var saved = settings.Current.ExternalProxy;
        saved.Protocol.ShouldBe(ExternalProxyProtocol.Https);
        saved.Host.ShouldBe("proxy.example.com");
        saved.Port.ShouldBe(8443);
        saved.Authentication.ShouldBe(ProxyAuthentication.UserPassword);
        saved.CredentialsKey.ShouldNotBeNullOrEmpty();
        settings.Current.Connection.ShouldBe(original.Connection, "внешний порт не подменяет локальный HTTP/SOCKS-вход");
        settings.Current.Protection.ShouldBe(original.Protection);
        settings.Current.KillSwitch.ShouldBe(original.KillSwitch);
        catalog.Selected.ShouldBe(vpn);
        Dispatcher.UIThread.RunJobs();
        Find<TextBlock>(window, "ExternalProxySaveState").Text.ShouldBe("Прокси сохранен");
        Find<TextBox>(window, "ExternalProxyHost").IsEffectivelyVisible.ShouldBeFalse();
        Find<Button>(window, "EditExternalProxy").IsEffectivelyVisible.ShouldBeTrue();
        Press(window, "EditExternalProxy");
        Dispatcher.UIThread.RunJobs();
        Find<TextBox>(window, "ExternalProxyHost").IsEffectivelyVisible.ShouldBeTrue();
        Find<TextBox>(window, "ExternalProxyHost").Text.ShouldBe(saved.Host);

        settings.Update(s => s with { Mode = ConnectionMode.Vpn });
        settings.Current.ExternalProxy.ShouldBe(saved);
        settings.Update(s => s with { Mode = ConnectionMode.Proxy });
        settings.Current.ExternalProxy.ShouldBe(saved);
        catalog.Selected.ShouldBe(vpn);
        using var reopened = new ExternalProxyEditorViewModel(settings, Service<IExternalProxyStore>());
        WaitFor(() => !reopened.IsBusy);
        reopened.Username.ShouldBe("proxy-test-user");
        reopened.Password.ShouldBe("proxy-test-pass");
        reopened.HasChanges.ShouldBeFalse();
        var metadata = JsonSerializer.Serialize(settings.Current);
        metadata.ShouldNotContain("proxy-test-user");
        metadata.ShouldNotContain("proxy-test-pass");
    }

    [AvaloniaTheory]
    [InlineData("host", "https://user:secret@proxy.example.com/path", "без протокола")]
    [InlineData("port", "65536", "от 1 до 65535")]
    [InlineData("credentials", "", "нужны оба значения")]
    public async Task Invalid_drafts_remain_inline_and_do_not_replace_the_saved_endpoint(string field, string value, string expected)
    {
        var (window, shell) = Open();
        SaveProxy();
        var settings = Service<SettingsService>();
        settings.Update(s => s with { Mode = ConnectionMode.Proxy });
        var editor = Settings(shell).ExternalProxy;
        editor.EditCommand.Execute(null);
        var before = settings.Current.ExternalProxy;
        switch (field)
        {
            case "host": editor.Host = value; break;
            case "port": editor.Port = value; break;
            default:
                editor.Authentication = ProxyAuthentication.UserPassword;
                editor.Username = "proxy-test-user";
                editor.Password = value;
                break;
        }

        await editor.SaveCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        editor.HasError.ShouldBeTrue();
        editor.Error.ShouldNotBeNull().ShouldContain(expected);
        settings.Current.ExternalProxy.ShouldBe(before);
        Find<TextBlock>(window, "ExternalProxyError").Text.ShouldBe(editor.Error);
        editor.Error.ShouldNotContain("user:secret");
    }

    [AvaloniaFact]
    public async Task Unavailable_secret_storage_blocks_password_save_but_anonymous_proxy_remains_supported()
    {
        var (_, shell) = Open();
        Secrets.Available = false;
        var settings = Service<SettingsService>();
        settings.Update(s => s with { Mode = ConnectionMode.Proxy });
        var editor = Settings(shell).ExternalProxy;
        editor.Host = "proxy.example.com";
        editor.Port = "1080";
        editor.Authentication = ProxyAuthentication.UserPassword;
        editor.Username = "proxy-test-user";
        editor.Password = "proxy-test-pass";
        await editor.SaveCommand.ExecuteAsync(null);

        editor.Error.ShouldNotBeNull().ShouldContain("хранилище ОС недоступно");
        settings.Current.ExternalProxy.ShouldBe(ExternalProxySettings.Default);
        Secrets.Values.ShouldBeEmpty();

        editor.Authentication = ProxyAuthentication.None;
        await editor.SaveCommand.ExecuteAsync(null);
        editor.HasError.ShouldBeFalse();
        settings.Current.ExternalProxy.Authentication.ShouldBe(ProxyAuthentication.None);
        settings.Current.ExternalProxy.Host.ShouldBe("proxy.example.com");
    }

    [AvaloniaFact]
    public async Task An_unconfigured_proxy_opens_settings_instead_of_connecting_the_saved_vpn()
    {
        var (_, shell) = Open();
        Service<IServerCatalog>().Selected.ShouldNotBeNull();
        Service<SettingsService>().Update(s => s with { Mode = ConnectionMode.Proxy });
        Dispatcher.UIThread.RunJobs();
        shell.Protection.HasServer.ShouldBeFalse();
        shell.Protection.ActionText.ShouldBe("Настроить прокси");

        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        shell.CurrentKey.ShouldBe(Pages.Settings);
        shell.Protection.Status.State.ShouldBe(ConnectionState.Off);
    }

    [AvaloniaFact]
    public async Task Active_proxy_metadata_and_retry_never_use_the_vpn_selection()
    {
        ScriptedService? gateway = null;
        var (window, shell) = Open(gateway: sp => gateway = new ScriptedService(Time,
            () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed));
        AddVpn();
        var vpn = Service<IServerCatalog>().Selected.ShouldNotBeNull();
        SaveProxy(host: "old.proxy.example.com");
        Service<SettingsService>().Update(s => s with { Mode = ConnectionMode.Proxy });
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var first = gateway!.Status.ServerId;
        ExternalProxySettings.IsExternalId(first).ShouldBeTrue();
        shell.Navigate(Pages.Home);
        var main = shell.CurrentPage.ShouldBeOfType<MainPageViewModel>();
        main.ServerTitle.ShouldBe("Внешний прокси");
        main.RealIpState.ShouldBe("виден сайтам вне прокси");
        main.ServerMeta.ShouldContain("old.proxy.example.com");
        Find<Button>(window, "RefreshServers").IsVisible.ShouldBeFalse();
        main.QuickServers.ShouldBeEmpty();
        Service<TrayViewModel>().Quick().ShouldBeEmpty();
        Service<TrayViewModel>().Header.ShouldNotContain(vpn.Name);

        SaveProxy(host: "new.proxy.example.com");
        Dispatcher.UIThread.RunJobs();
        main.ServerMeta.Contains("old.proxy.example.com", StringComparison.Ordinal).ShouldBeTrue(
            "снимок активной сессии не заменяется новыми настройками");
        main.ServerMeta.ShouldNotContain("new.proxy.example.com");
        Service<IServerCatalog>().Selected.ShouldBe(vpn);
        gateway.Push(s => s with { State = ConnectionState.Error, ErrorCode = ErrorCodes.ServerNotResponding });
        Dispatcher.UIThread.RunJobs();
        await Service<NoticeActions>().HandleAsync(new NoticeReply("test", NoticeAction.Retry));
        Dispatcher.UIThread.RunJobs();
        gateway.Status.ServerId.ShouldBe(Service<ConnectionTarget>().Selected!.Id);
        gateway.Status.ServerId.ShouldNotBe(vpn.Id);
        main.ServerMeta.ShouldContain("new.proxy.example.com");
        main.ToggleSwitchCommand.Execute(null);
        shell.CurrentKey.ShouldBe(Pages.Settings);
    }

    [AvaloniaFact]
    public void Missing_saved_credentials_are_visible_without_changing_the_authentication_mode()
    {
        var (_, shell) = Open();
        var store = Service<IExternalProxyStore>();
        store.Save(new ExternalProxySettings(Host: "proxy.example.com", Authentication: ProxyAuthentication.UserPassword),
            "proxy-test-user", "proxy-test-pass");
        Secrets.Values.Clear();
        var settings = Service<SettingsService>();
        settings.Update(s => s with { Mode = ConnectionMode.Proxy });
        var editor = Settings(shell).ExternalProxy;
        WaitFor(() => !editor.IsBusy);

        editor.RequiresCredentials.ShouldBeTrue();
        editor.SaveState.ShouldContain("Введите логин и пароль заново");
        editor.IsEditing.ShouldBeTrue();
        settings.Current.ExternalProxy.Authentication.ShouldBe(ProxyAuthentication.UserPassword);
        store.Definition().ShouldBeNull("потеря секрета не разрешает анонимное подключение");
    }

    [AvaloniaFact]
    public void Reports_and_failure_notices_do_not_include_external_credentials_or_secret_references()
    {
        var (_, shell) = Open();
        Service<IExternalProxyStore>().Save(new ExternalProxySettings(Host: "proxy.example.com", Port: 8443,
            Protocol: ExternalProxyProtocol.Https, Authentication: ProxyAuthentication.UserPassword), "proxy-test-user", "proxy-test-pass");
        Service<SettingsService>().Update(s => s with { Mode = ConnectionMode.Proxy });
        shell.Navigate(Pages.Check);
        var report = shell.CurrentPage.ShouldBeOfType<CheckPageViewModel>().BuildReport();

        report.ShouldContain("Внешний прокси: конфигурация подключения");
        report.ShouldContain("HTTPS");
        report.ShouldContain("Локальный вход приложения: порт " + Service<SettingsService>().Current.Connection.ProxyPort);
        report.ShouldNotContain("proxy-test-user");
        report.ShouldNotContain("proxy-test-pass");
        report.ShouldNotContain("proxy.example.com");
        report.ShouldNotContain(Service<SettingsService>().Current.ExternalProxy.CredentialsKey!);
        var notice = new NoticeTexts().Text(new Notice(NoticeKind.ConnectionFailed, "notify.connectRejected.proxy", [NoticeAction.ChangeServer])
        { ServerId = Service<ConnectionTarget>().Selected!.Id });
        notice.Body.ShouldContain("не ответил или не принял данные доступа");
        notice.Body.ShouldNotContain("proxy-test-pass");
        notice.Buttons.Single().Label.ShouldBe("Настроить");
    }

    [AvaloniaFact]
    public async Task Waiting_for_protected_storage_keeps_the_window_responsive_and_preserves_a_newer_draft()
    {
        var (window, shell) = Open();
        Service<SettingsService>().Update(s => s with { Mode = ConnectionMode.Proxy });
        Settings(shell);
        using var gate = new GatedStore(Service<IExternalProxyStore>());
        using var editor = new ExternalProxyEditorViewModel(Service<SettingsService>(), gate);
        Find<Control>(window, "ExternalProxyEditor").DataContext = editor;
        editor.Host = "proxy.example.com";
        editor.Port = "1080";

        var save = editor.SaveCommand.ExecuteAsync(null);
        WaitFor(() => gate.SaveEntered.IsSet);
        save.IsCompleted.ShouldBeFalse("хранилище еще ожидает ответ, но вызов уже вернул управление окну");
        editor.IsBusy.ShouldBeTrue();
        Find<StackPanel>(window, "ExternalProxyEditorBody").IsEnabled.ShouldBeFalse();
        var pulse = false;
        Dispatcher.UIThread.Post(() => pulse = true);
        Dispatcher.UIThread.RunJobs();
        pulse.ShouldBeTrue("ожидание Keychain/Secret Service не занимает поток окна");

        editor.Host = "new.proxy.example.com";
        gate.SaveRelease.Set();
        await save;
        Dispatcher.UIThread.RunJobs();

        editor.IsBusy.ShouldBeFalse();
        editor.Host.ShouldBe("new.proxy.example.com", "позднее завершение не стирает более новый черновик");
        editor.HasChanges.ShouldBeTrue();
        Service<SettingsService>().Current.ExternalProxy.Host.ShouldBe("proxy.example.com");
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task A_delayed_save_completion_reloads_the_current_profiles_own_credentials(bool anonymousFirst, bool sameEndpoint)
    {
        var (_, shell) = Open();
        var settings = Service<SettingsService>();
        settings.Update(s => s with { Mode = ConnectionMode.Proxy });
        var store = Service<IExternalProxyStore>();
        using var gate = new CommitBeforeReturnStore(store);
        using var editor = new ExternalProxyEditorViewModel(settings, gate);
        editor.Host = "first.proxy.example.com";
        editor.Port = "1080";
        editor.Authentication = anonymousFirst ? ProxyAuthentication.None : ProxyAuthentication.UserPassword;
        editor.Username = anonymousFirst ? string.Empty : "first-account";
        editor.Password = anonymousFirst ? string.Empty : "first-password";

        var save = editor.SaveCommand.ExecuteAsync(null);
        WaitFor(() => gate.SavedBeforeReturn.IsSet);
        var committedFirst = settings.Current.ExternalProxy;
        var currentHost = sameEndpoint ? "first.proxy.example.com" : "restored.proxy.example.com";
        store.Save(new ExternalProxySettings(Host: currentHost, Port: 1080, Authentication: ProxyAuthentication.UserPassword),
            "restored-account", "restored-password");
        var current = settings.Current.ExternalProxy;
        current.Revision.ShouldBeGreaterThan(committedFirst.Revision);
        Dispatcher.UIThread.RunJobs();

        gate.ReturnRelease.Set();
        await save;
        await editor.CredentialsRead;
        Dispatcher.UIThread.RunJobs();

        editor.Host.ShouldBe(currentHost);
        editor.Authentication.ShouldBe(ProxyAuthentication.UserPassword);
        editor.Username.ShouldBe("restored-account", "другая страница уже опубликовала свою редакцию и ее секрет");
        editor.Password.ShouldBe("restored-password");
        editor.HasChanges.ShouldBeFalse();

        // Повторное сохранение не должно отправить старые данные доступа новому адресу.
        editor.EditCommand.Execute(null);
        await editor.SaveCommand.ExecuteAsync(null);
        var credentials = store.ReadCredentials().ShouldNotBeNull();
        credentials.Username.ShouldBe("restored-account");
        credentials.Password.ShouldBe("restored-password");
        store.Definition().ShouldNotBeNull().Host.ShouldBe(currentHost);
    }

    [AvaloniaFact]
    public async Task Disposing_the_form_does_not_restore_credentials_from_a_late_protected_read()
    {
        var (_, shell) = Open();
        Service<IExternalProxyStore>().Save(new ExternalProxySettings(Host: "proxy.example.com", Authentication: ProxyAuthentication.UserPassword),
            "proxy-test-user", "proxy-test-pass");
        using var gate = new GatedStore(Service<IExternalProxyStore>()) { BlockRead = true };
        using var editor = new ExternalProxyEditorViewModel(Service<SettingsService>(), gate);
        gate.ReadEntered.IsSet.ShouldBeFalse("скрытая форма режима VPN не запрашивает Keychain внешнего прокси");
        Service<SettingsService>().Update(s => s with { Mode = ConnectionMode.Proxy });
        WaitFor(() => gate.ReadEntered.IsSet);
        editor.IsBusy.ShouldBeTrue("защищенное чтение еще не завершилось, а конструктор вернул управление");

        editor.Dispose();
        gate.ReadRelease.Set();
        await editor.CredentialsRead;
        Dispatcher.UIThread.RunJobs();

        editor.Username.ShouldBeEmpty();
        editor.Password.ShouldBeEmpty();
    }

    [AvaloniaFact]
    public async Task A_vpn_card_does_not_claim_to_be_active_under_an_external_proxy_and_can_explicitly_connect_vpn()
    {
        ScriptedService? gateway = null;
        var (_, shell) = Open(gateway: sp => gateway = new ScriptedService(Time,
            () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed));
        AddVpn();
        SaveProxy();
        var settings = Service<SettingsService>();
        var proxy = settings.Current.ExternalProxy;
        var vpn = Service<IServerCatalog>().Selected.ShouldNotBeNull();
        settings.Update(s => s with { Mode = ConnectionMode.Proxy });
        await shell.Protection.ToggleCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        shell.Navigate(Pages.Servers);
        var servers = shell.CurrentPage.ShouldBeOfType<ServersPageViewModel>();
        var row = servers.Groups.SelectMany(g => g.Rows).Single(r => r.Id == vpn.Id);
        row.Tag.ShouldBe("Выбран для VPN");
        row.OpenCommand.Execute(row.Id);
        Dispatcher.UIThread.RunJobs();
        var card = shell.Overlay.Sheet.ShouldBeOfType<ServerCardViewModel>();
        card.IsInUse.ShouldBeFalse();
        card.ConnectText.ShouldBe("Подключить VPN");
        var connect = card.ConnectCommand.ExecuteAsync(null);
        settings.Current.Mode.ShouldBe(ConnectionMode.Proxy);
        shell.Overlay.Dialog.ShouldNotBeNull().SecondaryCommand.Execute(null);
        await connect;
        Dispatcher.UIThread.RunJobs();

        settings.Current.Mode.ShouldBe(ConnectionMode.Vpn);
        settings.Current.ExternalProxy.ShouldBe(proxy);
        gateway!.Status.ServerId.ShouldBe(vpn.Id);
        gateway.Status.Mode.ShouldBe(ConnectionMode.Vpn);
    }

    private static SettingsPageViewModel Settings(ShellViewModel shell)
    {
        shell.Navigate(Pages.Settings);
        Dispatcher.UIThread.RunJobs();
        return shell.CurrentPage.ShouldBeOfType<SettingsPageViewModel>();
    }

    private void SaveProxy(string host = "proxy.example.com") => Service<IExternalProxyStore>().Save(
        new ExternalProxySettings(Host: host, Port: 8080), string.Empty, string.Empty);

    private void AddVpn() => Service<IServerStore>().Add(
        [LinkParser.Parse("trojan://fixture-password@198.51.100.1:443#VPN%20example").Server!], null);

    private sealed class GatedStore(IExternalProxyStore inner) : IExternalProxyStore, IDisposable
    {
        public ManualResetEventSlim SaveEntered { get; } = new();

        public ManualResetEventSlim SaveRelease { get; } = new();

        public ManualResetEventSlim ReadEntered { get; } = new();

        public ManualResetEventSlim ReadRelease { get; } = new();

        public ManualResetEventSlim ReadFinished { get; } = new();

        public bool BlockRead { get; init; }

        public bool CanStoreSecrets => inner.CanStoreSecrets;

        public string SecretStoreName => inner.SecretStoreName;

        public ServerCredentials? ReadCredentials()
        {
            if (!BlockRead)
            {
                return inner.ReadCredentials();
            }

            ReadEntered.Set();
            try
            {
                if (!ReadRelease.Wait(TimeSpan.FromSeconds(3)))
                {
                    throw new InvalidOperationException("Защищенное чтение не завершилось вовремя");
                }

                return inner.ReadCredentials();
            }
            finally
            {
                ReadFinished.Set();
            }
        }

        public void Save(ExternalProxySettings settings, string username, string password)
        {
            SaveEntered.Set();
            if (!SaveRelease.Wait(TimeSpan.FromSeconds(3)))
            {
                throw new InvalidOperationException("Защищенное сохранение не завершилось вовремя");
            }

            inner.Save(settings, username, password);
        }

        public ServerDefinition? Definition() => inner.Definition();

        public void Dispose()
        {
            SaveRelease.Set();
            ReadRelease.Set();
            SaveEntered.Dispose();
            SaveRelease.Dispose();
            ReadEntered.Dispose();
            ReadRelease.Dispose();
            ReadFinished.Dispose();
        }
    }

    private sealed class CommitBeforeReturnStore(IExternalProxyStore inner) : IExternalProxyStore, IDisposable
    {
        public ManualResetEventSlim SavedBeforeReturn { get; } = new();

        public ManualResetEventSlim ReturnRelease { get; } = new();

        public bool CanStoreSecrets => inner.CanStoreSecrets;

        public string SecretStoreName => inner.SecretStoreName;

        public ServerCredentials? ReadCredentials() => inner.ReadCredentials();

        public void Save(ExternalProxySettings settings, string username, string password)
        {
            inner.Save(settings, username, password);
            SavedBeforeReturn.Set();
            if (!ReturnRelease.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new InvalidOperationException("Завершение сохранения не дождалось подтверждения стенда");
            }
        }

        public ServerDefinition? Definition() => inner.Definition();

        public void Dispose()
        {
            ReturnRelease.Set();
            SavedBeforeReturn.Dispose();
            ReturnRelease.Dispose();
        }
    }
}
