using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Account;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Account;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Service;
using PrivateGateway.Client.Infrastructure.Storage;
using PrivateGateway.Client.UI;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// Вход в аккаунт сайта (доска 10, перед приветствием; решения 621, 626): ворота до любого экрана, форма в два шага
/// (почта и пароль, а администратору — код 2FA), ошибки сайта, потеря доступа и выход в «Настройках». Сервис входа
/// настоящий, сайт — подделка без сети.
/// </summary>
public sealed class LoginPageTests : WindowHarness
{
    [AvaloniaFact]
    public void Without_sign_in_the_window_opens_on_login_before_the_welcome_and_nothing_else_opens()
    {
        var (window, shell, page) = OpenLogin(firstLaunch: true);

        (shell.CurrentKey, shell.IsIslandVisible).ShouldBe((Pages.Login, false));
        Text(window, "LoginTitle").ShouldBe("Вход в Private Gateway");
        Find<Border>(window, "LoginNotice").IsVisible.ShouldBeFalse("первый вход — без причины");
        Find<TextBlock>(window, "LoginError").IsVisible.ShouldBeFalse();
        Find<Button>(window, "SignInButton").IsEffectivelyEnabled.ShouldBeFalse("поля пустые");
        Text(window, "LoginText").ShouldBe("Войдите аккаунтом сайта Private Gateway Web: те же почта и пароль, что и на сайте.");
        (Find<StackPanel>(window, "LoginCredentials").IsVisible, Find<StackPanel>(window, "LoginCodeStep").IsVisible,
            Find<Button>(window, "LoginBack").IsVisible).ShouldBe((true, false, false), "кода на первом шаге нет: 2FA — только у администраторов");
        Snapshot(window, "login");

        foreach (var other in new[] { Pages.Home, Pages.Welcome, Pages.Settings, Pages.Add, Pages.Problem })
        {
            shell.Navigate(other);
            Dispatcher.UIThread.RunJobs();
            shell.CurrentKey.ShouldBe(Pages.Login, $"без входа «{other}» не открывается");
        }

        shell.CurrentPage.ShouldBeSameAs(page);
        Service<SettingsService>().Current.OnboardingDone.ShouldBeFalse("приветствие еще впереди");
    }

    [AvaloniaFact]
    public void Language_is_chosen_on_the_login_screen()
    {
        var (window, _, page) = OpenLogin(firstLaunch: true);

        Find<RadioButton>(window, "LoginEn").IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        (Loc.Instance.Language, Service<SettingsService>().Current.Language).ShouldBe(("en", "en"));
        Text(window, "LoginTitle").ShouldBe("Sign in to Private Gateway");
        Text(window, "LoginText").ShouldBe("Use your Private Gateway Web account: the same email and password as on the site.");
        page.IsRussian.ShouldBeFalse();
        Snapshot(window, "login-en");
    }

    [AvaloniaFact]
    public void Sign_in_keeps_the_token_in_the_os_store_and_goes_on_to_the_welcome()
    {
        var (window, shell, page) = OpenLogin(firstLaunch: true);

        Fill(window, " anna@example.com ", "secret pass");
        Press(window, "SignInButton");
        WaitFor(() => shell.CurrentKey == Pages.Welcome);

        shell.IsIslandVisible.ShouldBeFalse("дальше — приветствие первого запуска");
        var request = AccountApi.Logins.ShouldHaveSingleItem();
        (request.Email, request.Password, request.Code, request.RecoveryCode).ShouldBe(("anna@example.com", "secret pass", null, null),
            "участник входит почтой и паролем, без кода");
        Secrets.Values[AccountService.TokenKey].ShouldBe(FakeAccountApi.IssuedToken);
        (page.Email, page.Password, page.Code).ShouldBe((" anna@example.com ", "", ""), "пароль и код не остаются в памяти");
    }

    [AvaloniaFact]
    public void Sign_in_after_the_first_launch_goes_home_with_the_island()
    {
        var (window, shell, _) = OpenLogin();

        Fill(window, FakeAccountApi.Email, "pw");
        Press(window, "SignInButton");
        WaitFor(() => shell.CurrentKey == Pages.Home);

        shell.IsIslandVisible.ShouldBeTrue();
        shell.Navigate(Pages.Login);
        Dispatcher.UIThread.RunJobs();
        shell.CurrentKey.ShouldBe(Pages.Home, "с действующим входом экран входа не открывается");
    }

    public static TheoryData<AccountFailure, string> Refusals => new()
    {
        { AccountFailure.InvalidCredentials, "Неверная почта или пароль." },
        { AccountFailure.InvalidCode, "Код неверный или уже использован. Введите новый." },
        { AccountFailure.SetupRequired, "Для аккаунта администратора нужна двухфакторная защита, а она еще не настроена. Войдите на сайт, настройте ее и вернитесь." },
        { AccountFailure.DeviceLimit, "Аккаунт уже используется на наибольшем числе устройств. Удалите старое устройство на сайте, чтобы освободить место." },
        { AccountFailure.DeviceChangeLimit, "За 30 дней к аккаунту добавлено слишком много новых устройств. Проверьте устройства на сайте или обратитесь к администратору." },
        { AccountFailure.RateLimited, "Слишком много попыток. Повторите через 2 мин." },
        { AccountFailure.Validation, "Проверьте почту. Код 2FA — ровно 6 цифр." },
        { AccountFailure.Network, "Нет связи с сайтом. Проверьте подключение к интернету и повторите." },
        { AccountFailure.Server, "Сайт ответил ошибкой. Повторите позже." },
    };

    [AvaloniaTheory]
    [MemberData(nameof(Refusals))]
    public void Each_refusal_of_the_site_has_its_own_text_and_keeps_the_email(AccountFailure failure, string text)
    {
        AccountApi.LoginFailure = failure;
        AccountApi.RetryAfter = TimeSpan.FromSeconds(61);
        var (window, shell, page) = OpenLogin();

        Fill(window, FakeAccountApi.Email, "pw");
        Press(window, "SignInButton");
        WaitFor(() => !page.IsBusy);

        shell.CurrentKey.ShouldBe(Pages.Login);
        page.IsCodeStep.ShouldBeFalse();
        Find<TextBlock>(window, "LoginError").IsVisible.ShouldBeTrue();
        Text(window, "LoginError").ShouldBe(text);
        Find<Button>(window, "LoginOpenSite").IsVisible.ShouldBe(failure == AccountFailure.SetupRequired, "«Открыть сайт» — только у 403");
        Find<Button>(window, "LoginOpenDevices").IsVisible.ShouldBe(failure is AccountFailure.DeviceLimit or AccountFailure.DeviceChangeLimit,
            "«Устройства на сайте» — только у 409");
        Secrets.Values.ShouldNotContainKey(AccountService.TokenKey);
        (page.Email, page.Password, page.Code).ShouldBe((FakeAccountApi.Email, "", ""));
        Find<Button>(window, "SignInButton").IsEffectivelyEnabled.ShouldBeFalse("пароль нужно ввести заново");
    }

    [AvaloniaFact]
    public void Error_text_follows_the_language()
    {
        AccountApi.LoginFailure = AccountFailure.RateLimited;
        AccountApi.RetryAfter = TimeSpan.FromMinutes(5);
        var (window, _, page) = OpenLogin();
        Fill(window, FakeAccountApi.Email, "pw");
        Press(window, "SignInButton");
        WaitFor(() => page.HasError);

        Find<RadioButton>(window, "LoginEn").IsChecked = true;
        Dispatcher.UIThread.RunJobs();

        Text(window, "LoginError").ShouldBe("Too many attempts. Try again in 5 min.");
    }

    [AvaloniaFact]
    public void Without_the_os_key_store_sign_in_is_refused_before_asking_the_site()
    {
        Secrets.Available = false;
        var (window, _, page) = OpenLogin();

        Fill(window, FakeAccountApi.Email, "pw");
        Press(window, "SignInButton");
        WaitFor(() => page.HasError);

        Text(window, "LoginError").ShouldBe("Хранилище ключей системы недоступно, поэтому вход не сохранить на этом устройстве.");
        AccountApi.Logins.ShouldBeEmpty("токен без защищенного хранилища не держим");
    }

    [AvaloniaFact]
    public void Setup_required_opens_the_login_page_of_the_site_and_device_limit_the_devices_page()
    {
        AccountApi.LoginFailure = AccountFailure.SetupRequired;
        var (window, _, page) = OpenLogin();
        Fill(window, FakeAccountApi.Email, "pw");
        Press(window, "SignInButton");
        WaitFor(() => page.CanOpenSite);
        Press(window, "LoginOpenSite");

        AccountApi.LoginFailure = AccountFailure.DeviceLimit;
        Fill(window, FakeAccountApi.Email, "pw");
        Press(window, "SignInButton");
        WaitFor(() => page.CanOpenDevices);
        Find<Button>(window, "LoginOpenSite").IsVisible.ShouldBeFalse();
        Snapshot(window, "login-error");
        Press(window, "LoginOpenDevices");

        AccountApi.LoginFailure = AccountFailure.DeviceChangeLimit;
        Fill(window, FakeAccountApi.Email, "pw");
        Press(window, "SignInButton");
        WaitFor(() => page.Failure == AccountFailure.DeviceChangeLimit);
        page.CanOpenDevices.ShouldBeTrue("частая смена устройств — та же кнопка «Устройства на сайте»");
        Snapshot(window, "login-device-changes");
        Press(window, "LoginOpenDevices");

        Urls.Opened.ShouldBe([AccountSite.LoginPage, AccountSite.DevicesPage, AccountSite.DevicesPage]);
        Urls.Opened.ShouldAllBe(url => url.Scheme == Uri.UriSchemeHttps);
    }

    [AvaloniaFact]
    public void Enter_in_the_form_signs_in_once_the_fields_are_filled()
    {
        var (window, shell, _) = OpenLogin();
        var password = Find<TextBox>(window, "LoginPassword");

        Fill(window, FakeAccountApi.Email, "");
        password.Focus();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        AccountApi.Logins.ShouldBeEmpty("пароль пустой — форма не готова");

        password.Text = "pw";
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        WaitFor(() => shell.CurrentKey == Pages.Home);
        AccountApi.Logins.ShouldHaveSingleItem().Code.ShouldBeNull();
    }

    [AvaloniaFact]
    public void Enter_in_the_code_field_signs_in_once_there_are_six_digits()
    {
        AccountApi.RequiresCode = true;
        var (window, shell, page) = ToCodeStep();
        var code = Find<TextBox>(window, "LoginCode");

        code.Text = "12345";
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        AccountApi.Logins.Count.ShouldBe(1, "в коде 2FA пять цифр — второй шаг не готов");

        code.Text = "123456";
        Dispatcher.UIThread.RunJobs();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        WaitFor(() => shell.CurrentKey == Pages.Home);
        AccountApi.Logins.Select(r => r.Code).ShouldBe([null, "123456"]);
        (page.IsCodeStep, page.Password).ShouldBe((false, ""));
    }

    [AvaloniaFact]
    public void Administrator_enters_the_code_on_a_second_step_and_the_password_waits_in_memory()
    {
        AccountApi.RequiresCode = true;
        var (window, shell, page) = OpenLogin();

        Fill(window, FakeAccountApi.Email, "secret pass");
        Press(window, "SignInButton");
        WaitFor(() => page.IsCodeStep);

        (Find<StackPanel>(window, "LoginCredentials").IsVisible, Find<StackPanel>(window, "LoginCodeStep").IsVisible,
            Find<Button>(window, "LoginBack").IsVisible).ShouldBe((false, true, true));
        Text(window, "LoginTitle").ShouldBe("Код подтверждения администратора");
        Text(window, "LoginText").ShouldBe("anna@example.com — аккаунт администратора: введите код двухфакторной защиты.");
        Text(window, "LoginCodeLabel").ShouldBe("Код 2FA");
        Find<TextBlock>(window, "LoginError").IsVisible.ShouldBeFalse("«нужен код» — не ошибка, а следующий шаг");
        Find<TextBox>(window, "LoginCode").IsFocused.ShouldBeTrue("код вводят сразу");
        (page.Email, page.Password, page.Code).ShouldBe((FakeAccountApi.Email, "secret pass", ""), "пароль ждет кода в памяти");
        Find<Button>(window, "SignInButton").IsEffectivelyEnabled.ShouldBeFalse("кода еще нет");
        Secrets.Values.ShouldNotContainKey(AccountService.TokenKey);
        Snapshot(window, "login-code");

        AccountApi.PendingLogin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Find<TextBox>(window, "LoginCode").Text = "654321";
        Dispatcher.UIThread.RunJobs();
        Press(window, "SignInButton");
        (Find<Button>(window, "SignInButton").Content, Find<Button>(window, "SignInButton").IsEffectivelyEnabled,
            Find<Button>(window, "LoginBack").IsEffectivelyEnabled).ShouldBe(("Вход…", false, false));

        AccountApi.PendingLogin.SetResult();
        WaitFor(() => shell.CurrentKey == Pages.Home);
        AccountApi.Logins.Select(r => (r.Email, r.Password, r.Code, r.RecoveryCode))
            .ShouldBe([(FakeAccountApi.Email, "secret pass", null, null), (FakeAccountApi.Email, "secret pass", "654321", null)]);
        (page.IsCodeStep, page.Password, page.Code).ShouldBe((false, "", ""), "вошли — пароль больше не нужен");
    }

    [AvaloniaFact]
    public void Wrong_code_keeps_the_second_step_and_the_password_for_the_next_code()
    {
        AccountApi.RequiresCode = true;
        AccountApi.LoginFailure = AccountFailure.InvalidCode;
        var (window, shell, page) = ToCodeStep();

        EnterCode(window, "111111");
        Press(window, "SignInButton");
        WaitFor(() => page.HasError);

        page.IsCodeStep.ShouldBeTrue();
        Text(window, "LoginError").ShouldBe("Код неверный или уже использован. Введите новый.");
        (page.Password, page.Code).ShouldBe(("pw", ""), "код стерт, пароль ждет следующего кода");
        Snapshot(window, "login-code-error");

        AccountApi.LoginFailure = null;
        EnterCode(window, "222222");
        Press(window, "SignInButton");
        WaitFor(() => shell.CurrentKey == Pages.Home);
        AccountApi.Logins.Select(r => r.Code).ShouldBe([null, "111111", "222222"]);
    }

    [AvaloniaFact]
    public void Another_account_returns_to_the_email_and_the_password()
    {
        AccountApi.RequiresCode = true;
        AccountApi.LoginFailure = AccountFailure.InvalidCode;
        var (window, _, page) = ToCodeStep();
        EnterCode(window, "111111");
        Press(window, "SignInButton");
        WaitFor(() => page.HasError);
        Press(window, "LoginToggleCode");

        Press(window, "LoginBack");
        Dispatcher.UIThread.RunJobs();

        (page.IsCodeStep, page.IsRecovery, page.HasError).ShouldBe((false, false, false));
        (page.Email, page.Password, page.Code).ShouldBe((FakeAccountApi.Email, "", ""), "почта остается, пароль и код стерты");
        Find<StackPanel>(window, "LoginCredentials").IsVisible.ShouldBeTrue();
        Find<TextBox>(window, "LoginEmail").IsFocused.ShouldBeTrue("другой аккаунт — с почты");
        Text(window, "LoginTitle").ShouldBe("Вход в Private Gateway");
        Find<Button>(window, "SignInButton").IsEffectivelyEnabled.ShouldBeFalse("пароль нужно ввести");
    }

    [AvaloniaFact]
    public void Password_changed_while_the_code_was_typed_returns_to_the_first_step_with_the_error()
    {
        AccountApi.RequiresCode = true;
        var (window, _, page) = ToCodeStep();
        AccountApi.LoginFailure = AccountFailure.InvalidCredentials;

        EnterCode(window, "123456");
        Press(window, "SignInButton");
        WaitFor(() => page.HasError);

        page.IsCodeStep.ShouldBeFalse();
        Text(window, "LoginError").ShouldBe("Неверная почта или пароль.");
        (page.Email, page.Password, page.Code).ShouldBe((FakeAccountApi.Email, "", ""));
    }

    [AvaloniaFact]
    public void While_the_site_answers_the_button_says_signing_in_and_is_disabled()
    {
        AccountApi.PendingLogin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (window, shell, page) = OpenLogin();
        var button = Find<Button>(window, "SignInButton");

        Fill(window, FakeAccountApi.Email, "pw");
        button.IsEffectivelyEnabled.ShouldBeTrue();
        Press(window, "SignInButton");

        page.IsBusy.ShouldBeTrue();
        (button.Content, button.IsEffectivelyEnabled).ShouldBe(("Вход…", false));
        Find<TextBox>(window, "LoginPassword").Focus();
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        AccountApi.Logins.Count.ShouldBe(1, "второй запрос не уходит, пока идет первый");

        AccountApi.PendingLogin.SetResult();
        WaitFor(() => shell.CurrentKey == Pages.Home);
        (page.IsBusy, page.SignInText).ShouldBe((false, "Войти"));
    }

    [AvaloniaFact]
    public void Code_field_takes_six_digits_and_switches_to_a_recovery_code()
    {
        AccountApi.RequiresCode = true;
        var (window, shell, page) = ToCodeStep();
        var code = Find<TextBox>(window, "LoginCode");

        code.Text = "12 34-5678";
        Dispatcher.UIThread.RunJobs();
        page.Code.ShouldBe("123456", "только цифры, не больше шести");

        Press(window, "LoginToggleCode");
        (page.IsRecovery, page.Code).ShouldBe((true, ""));
        Text(window, "LoginCodeLabel").ShouldBe("Резервный код");
        Text(window, "LoginCodeHint").ShouldBe("Один из кодов, сохраненных при настройке 2FA");
        Find<Button>(window, "LoginToggleCode").Content.ShouldBe("Войти по коду 2FA");
        Snapshot(window, "login-recovery");

        EnterCode(window, " abcd-efgh ");
        Press(window, "SignInButton");
        WaitFor(() => shell.CurrentKey == Pages.Home);
        var request = AccountApi.Logins[^1];
        (request.Password, request.Code, request.RecoveryCode).ShouldBe(("pw", null, "abcd-efgh"));
    }

    [AvaloniaFact]
    public void Eye_shows_the_password()
    {
        var (window, _, page) = OpenLogin();
        var password = Find<TextBox>(window, "LoginPassword");

        password.RevealPassword.ShouldBeFalse();
        Press(window, "LoginShowPassword");
        (page.IsPasswordVisible, password.RevealPassword).ShouldBe((true, true));
    }

    [AvaloniaFact]
    public async Task Revoked_access_turns_protection_off_and_returns_to_login_with_the_reason()
    {
        var (window, shell) = Open(account: true, signedIn: true);
        shell.CurrentKey.ShouldBe(Pages.Home, "вход сохранен с прошлого запуска");
        await Connect(shell);
        var overlay = Service<OverlayViewModel>();
        shell.Navigate(Pages.Settings);
        Dispatcher.UIThread.RunJobs();
        var signOut = ((SettingsPageViewModel)shell.CurrentPage!).SignOutCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        overlay.HasDialog.ShouldBeTrue();

        AccountApi.StatusFailure = AccountFailure.InvalidCredentials;
        await Service<AccountService>().CheckAsync();
        WaitFor(() => shell.CurrentKey == Pages.Login);
        WaitFor(() => !overlay.HasDialog);
        await signOut.WaitAsync(TimeSpan.FromSeconds(5));

        AccountApi.Logouts.ShouldBeEmpty("вопрос о выходе снят безопасным ответом: доступа уже нет");
        shell.IsIslandVisible.ShouldBeFalse();
        shell.Protection.Status.State.ShouldBe(ConnectionState.Off, "отзыв выключает защиту");
        Secrets.Values.ShouldNotContainKey(AccountService.TokenKey);
        Find<Border>(window, "LoginNotice").IsVisible.ShouldBeTrue();
        Text(window, "LoginNoticeText").ShouldBe("Доступ отозван на сайте, защита выключена. Войдите снова или обратитесь к администратору.");
        Snapshot(window, "login-revoked");

        Fill(window, FakeAccountApi.Email, "pw");
        Press(window, "SignInButton");
        WaitFor(() => shell.CurrentKey == Pages.Home);
    }

    [AvaloniaFact]
    public async Task Without_the_site_access_lasts_until_the_confirmed_date_then_the_sign_in_expires()
    {
        var (window, shell) = Open(account: true, signedIn: true);
        AccountApi.StatusFailure = AccountFailure.Network;

        await Service<AccountService>().CheckAsync();
        Dispatcher.UIThread.RunJobs();
        shell.CurrentKey.ShouldBe(Pages.Home, "нет связи — не отзыв");

        Time.SetUtcNow(FakeAccountApi.Expires);
        await Service<AccountService>().CheckAsync();
        WaitFor(() => shell.CurrentKey == Pages.Login);

        Text(window, "LoginNoticeText").ShouldBe("Срок входа истек. Войдите снова.");
    }

    [AvaloniaFact]
    public void Demo_mode_skips_the_sign_in_and_the_working_mode_requires_it()
    {
        // Состав программы: в демо-режиме (--demo) вход не нужен, в рабочем — нужен.
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "pg-ui-" + Guid.NewGuid().ToString("N")));
        using (var demo = App.BuildServices(paths, demo: true))
        {
            demo.GetRequiredService<AccountOptions>().Required.ShouldBeFalse();
        }

        using (var working = App.BuildServices(paths, demo: false))
        {
            working.GetRequiredService<AccountOptions>().Required.ShouldBeTrue();
        }

        Directory.Exists(paths.DataDirectory).ShouldBeFalse();
        var (window, shell) = Open(firstLaunch: true);

        Service<AccountService>().IsRequired.ShouldBeFalse();
        shell.CurrentKey.ShouldBe(Pages.Welcome);
        shell.Navigate(Pages.Login);
        Dispatcher.UIThread.RunJobs();
        shell.CurrentKey.ShouldNotBe(Pages.Login);
        shell.Navigate(Pages.Settings);
        Dispatcher.UIThread.RunJobs();
        Find<StackPanel>(window, "AccountSection").IsVisible.ShouldBeFalse("в демо-режиме аккаунта нет");
    }

    [AvaloniaFact]
    public void Service_problem_does_not_leave_login_and_shows_after_sign_in()
    {
        ScriptedService? service = null;
        var (window, shell) = Open(account: true,
            gateway: sp => service = new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed));
        shell.CurrentKey.ShouldBe(Pages.Login);

        service.ShouldNotBeNull().Push(s => s with { State = ConnectionState.Error, ErrorCode = ErrorCodes.ServiceNotRunning });
        Dispatcher.UIThread.RunJobs();
        shell.CurrentKey.ShouldBe(Pages.Login, "ошибка службы не уводит со входа");

        Fill(window, FakeAccountApi.Email, "pw");
        Press(window, "SignInButton");
        WaitFor(() => shell.CurrentKey == Pages.Problem);
        Text(window, "ProblemTitle").ShouldBe("Служба не запущена");
    }

    [AvaloniaFact]
    public async Task Sign_out_in_settings_asks_first_then_turns_protection_off_and_opens_an_empty_login()
    {
        var (window, shell, page) = OpenLogin();
        Fill(window, FakeAccountApi.Email, "pw");
        Press(window, "SignInButton");
        WaitFor(() => shell.CurrentKey == Pages.Home);
        await Connect(shell);

        shell.Navigate(Pages.Settings);
        Dispatcher.UIThread.RunJobs();
        Find<StackPanel>(window, "AccountSection").IsVisible.ShouldBeTrue();
        Text(window, "AccountName").ShouldBe(FakeAccountApi.DisplayName);
        Text(window, "AccountEmail").ShouldBe(FakeAccountApi.Email);
        Text(window, "AccountDevice").ShouldBe("Это устройство: Ноутбук Анны");
        Snapshot(window, "settings-account");

        Press(window, "SignOut");
        shell.Overlay.Dialog.ShouldNotBeNull().Title.ShouldBe("Выйти из аккаунта?");
        Find<Button>(window, "DialogSecondary").Classes.ShouldContain("danger");
        Snapshot(window, "dialog-sign-out");
        shell.Overlay.Dialog!.PrimaryCommand.Execute(null);
        WaitFor(() => !shell.Overlay.HasDialog);
        (shell.CurrentKey, Secrets.Values.ContainsKey(AccountService.TokenKey)).ShouldBe((Pages.Settings, true), "«Остаться» ничего не меняет");

        Press(window, "SignOut");
        shell.Overlay.Dialog.ShouldNotBeNull().SecondaryCommand.Execute(null);
        WaitFor(() => shell.CurrentKey == Pages.Login);

        shell.Protection.Status.State.ShouldBe(ConnectionState.Off);
        Secrets.Values.ShouldNotContainKey(AccountService.TokenKey);
        AccountApi.Logouts.ShouldBe([FakeAccountApi.IssuedToken]);
        shell.CurrentPage.ShouldBeSameAs(page);
        (page.Email, page.Password, page.Code, page.HasError).ShouldBe(("", "", "", false), "после выхода форма пустая");
        Find<Border>(window, "LoginNotice").IsVisible.ShouldBeFalse("выход сам — без причины");
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task In_strict_mode_sign_out_and_revocation_open_the_network_for_the_next_sign_in(bool revoked)
    {
        var (_, shell) = Open(account: true, signedIn: true);
        Service<SettingsService>().Update(s => s with
        {
            Mode = ConnectionMode.Vpn,
            KillSwitch = true,
            Protection = s.Protection with { StrictMode = true },
        });
        await Connect(shell);

        if (revoked)
        {
            AccountApi.StatusFailure = AccountFailure.InvalidCredentials;
            await Service<AccountService>().CheckAsync();
        }
        else
        {
            await Service<AccountService>().SignOutAsync();
        }

        WaitFor(() => shell.CurrentKey == Pages.Login);
        (shell.Protection.Status.State, shell.Protection.Status.StrictBlock).ShouldBe((ConnectionState.Off, false),
            "без доступа защиты нет, а закрытая строгим режимом сеть не пустила бы вход до сайта");
    }

    public static TheoryData<string> UnexpectedFailures => new() { "io", "canceled", "other" };

    [AvaloniaTheory]
    [MemberData(nameof(UnexpectedFailures))]
    public void Unexpected_failure_while_signing_in_is_a_line_under_the_form_not_a_crash(string kind)
    {
        AccountApi.LoginException = kind switch
        {
            "io" => new IOException("обрыв посреди ответа"),
            "canceled" => new OperationCanceledException(),
            _ => new InvalidDataException("непредвиденное"),
        };
        var (window, shell, page) = OpenLogin();

        Fill(window, FakeAccountApi.Email, "pw");
        Press(window, "SignInButton");
        WaitFor(() => !page.IsBusy);

        shell.CurrentKey.ShouldBe(Pages.Login);
        Text(window, "LoginError").ShouldBe(kind == "other" ? "Сайт ответил ошибкой. Повторите позже." : "Нет связи с сайтом. Проверьте подключение к интернету и повторите.");
        (page.Password, page.Code).ShouldBe(("", ""));
    }

    [AvaloniaFact]
    public void Refusal_from_the_tray_before_sign_in_is_cleared_by_the_sign_in()
    {
        ScriptedService? service = null;
        var (window, shell) = Open(account: true, gateway: sp => new GatewayHost(
            new ServiceLocator([new ServiceAddress("installed", System: true)], _ => true, new NoLauncher(), Time),
            _ => service = new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed),
            () => sp.GetRequiredService<SettingsService>().Current, Time,
            mayConnect: () => sp.GetRequiredService<AccountService>().MayConnect));
        var tray = Service<TrayViewModel>();
        var off = (tray.Mark, tray.ToolTip);

        var store = Service<SqliteServerCatalog>();
        store.Add([LinkParser.Parse("trojan://fake@192.0.2.10:443#Проверка").Server!], null);
        store.Choose(store.Servers.Single().Id);
        Dispatcher.UIThread.RunJobs();

        Item(tray.Menu, "Включить защиту").Command!.Execute(null);
        WaitFor(() => shell.Protection.Status.ErrorCode == ErrorCodes.AccessRequired);
        service.ShouldNotBeNull().Status.State.ShouldBe(ConnectionState.Off, "служба просьбы не получила");
        (shell.Protection.Status.State, tray.Mark).ShouldBe((ConnectionState.Error, "Icon.Mark.Alert"));

        Fill(window, FakeAccountApi.Email, "pw");
        Press(window, "SignInButton");
        WaitFor(() => shell.CurrentKey == Pages.Home);

        (shell.Protection.Status.State, shell.Protection.Status.ErrorCode).ShouldBe((ConnectionState.Off, null),
            "вошли — «Нет доступа» на главной больше не правда");
        (tray.Mark, tray.ToolTip).ShouldBe(off, "и в трее");
    }

    private static NativeMenuItem Item(NativeMenu menu, string header) =>
        menu.Items.OfType<NativeMenuItem>().Single(i => i.Header == header);

    private sealed class NoLauncher : IUserServiceLauncher
    {
        public bool Start() => false;
    }

    private (MainWindow Window, ShellViewModel Shell, LoginPageViewModel Page) OpenLogin(bool firstLaunch = false)
    {
        var (window, shell) = Open(firstLaunch, account: true);
        return (window, shell, shell.CurrentPage.ShouldBeOfType<LoginPageViewModel>());
    }

    /// <summary>Аккаунт администратора (<see cref="FakeAccountApi.RequiresCode"/>): почта и пароль приняты, ждем код.</summary>
    private (MainWindow Window, ShellViewModel Shell, LoginPageViewModel Page) ToCodeStep()
    {
        var (window, shell, page) = OpenLogin();
        Fill(window, FakeAccountApi.Email, "pw");
        Press(window, "SignInButton");
        WaitFor(() => page.IsCodeStep);
        return (window, shell, page);
    }

    /// <summary>Первый шаг: почта и пароль.</summary>
    private static void Fill(MainWindow window, string email, string password)
    {
        Find<TextBox>(window, "LoginEmail").Text = email;
        Find<TextBox>(window, "LoginPassword").Text = password;
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Второй шаг: код 2FA или резервный код.</summary>
    private static void EnterCode(MainWindow window, string code)
    {
        Find<TextBox>(window, "LoginCode").Text = code;
        Dispatcher.UIThread.RunJobs();
    }
}
