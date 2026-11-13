using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Application.Account;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Application.Updates;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Account;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// Вход в аккаунт сайта (<see cref="AccountService"/>): токен только в хранилище ОС, отзыв только по 401, без сети —
/// до подтвержденного срока, выход выключает защиту. Сайт, хранилища и служба — подделки.
/// </summary>
public sealed class AccountServiceTests : IDisposable
{
    private const string Token = "tok-1";
    private static readonly DateTimeOffset Start = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Term = Start + TimeSpan.FromDays(30);
    private static readonly ServerProfile Nl =
        new("nl", "Нидерланды 01", "NL", ProxyProtocol.Vless, TransportSecurity.Reality, "203.0.113.24", 443, "tcp", 42);

    private readonly FakeTimeProvider _time = new(Start);
    private readonly FakeApi _api;
    private readonly MemoryAccountStore _store = new();
    private readonly FakeDevice _device = new();
    private readonly FakeSecrets _secrets = new();
    private readonly ConnectionControllerTests.FakeGateway _gateway = new(ConnectionState.Connected, "nl");
    private readonly List<AccountService> _services = [];

    public AccountServiceTests() => _api = new FakeApi(_time);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var service in _services)
        {
            service.Dispose();
        }
    }

    [Fact]
    public async Task Sign_in_keeps_the_token_in_the_os_store_and_the_next_start_finds_it()
    {
        var account = Service();
        account.IsRequired.ShouldBeTrue();
        (account.HasAccess, account.MayConnect, account.IsSignedIn).ShouldBe((false, false, false));

        var outcome = await account.SignInAsync(" user@example.com ", "pw", " 123456 ", "", Ct);

        outcome.ShouldBe(LoginOutcome.Success);
        _api.Logins.ShouldHaveSingleItem().ShouldBe(new LoginRequest("user@example.com", "pw", "123456", null,
            new DeviceInfo(_device.Uid, "WORKSTATION", DevicePlatform.Current, "0.2.0")));
        _secrets.Values.ShouldBe(new Dictionary<string, string> { [AccountService.TokenKey] = Token });
        _store.Saved.ShouldBe(new SavedAccount(Term, "user@example.com", "Роман", "WORKSTATION", Start));
        (account.HasAccess, account.Email, account.DisplayName, account.DeviceName, account.LastMessage)
            .ShouldBe((true, "user@example.com", "Роман", "WORKSTATION", AccountMessage.None));

        var restarted = Service();
        (restarted.HasAccess, restarted.Email, restarted.ExpiresAt).ShouldBe((true, "user@example.com", Term));
    }

    [Fact]
    public async Task Administrator_is_asked_for_a_code_and_nothing_is_saved_until_it_comes()
    {
        // 0.2.1: первый шаг — без кода; сайт ответил «нужен код» — входа еще нет, повтор — с кодом.
        _api.Login = _ => throw new AccountApiException(AccountFailure.CodeRequired, 401);
        var account = Service();

        (await account.SignInAsync("admin@example.com", "pw", null, " ", Ct)).ShouldBe(new LoginOutcome(AccountFailure.CodeRequired));

        (_secrets.Values.Count, _store.Saved, account.HasAccess, _gateway.Grants).ShouldBe((0, null, false, 0));
        _api.Login = null;
        (await account.SignInAsync("admin@example.com", "pw", "654321", null, Ct)).ShouldBe(LoginOutcome.Success);
        _api.Logins.Select(r => (r.Password, r.Code, r.RecoveryCode)).ShouldBe([("pw", null, null), ("pw", "654321", null)]);
        account.HasAccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Without_the_os_store_there_is_no_sign_in_and_no_request()
    {
        _secrets.Available = false;
        var account = Service();

        (await account.SignInAsync("user@example.com", "pw", "123456", null, Ct)).ShouldBe(new LoginOutcome(AccountFailure.Store));

        _api.Logins.ShouldBeEmpty("токен без защищенного хранилища не держим — и на сайт не ходим");
        account.HasAccess.ShouldBeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Store_failing_on_save_leaves_nothing_half_saved(bool secretsFail)
    {
        _secrets.SavesLeft = secretsFail ? 0 : null;
        _store.Fails = !secretsFail;
        var account = Service();

        (await account.SignInAsync("user@example.com", "pw", "123456", null, Ct)).Failure.ShouldBe(AccountFailure.Store);

        (_secrets.Values.Count, _store.Saved, account.HasAccess).ShouldBe((0, null, false));
    }

    [Fact]
    public async Task Refusal_of_the_site_comes_back_with_the_minutes_to_wait()
    {
        _api.Login = _ => throw new AccountApiException(AccountFailure.RateLimited, 429, TimeSpan.FromSeconds(61));
        var account = Service();

        (await account.SignInAsync("user@example.com", "pw", "123456", null, Ct)).ShouldBe(new LoginOutcome(AccountFailure.RateLimited, 2));

        (_secrets.Values.Count, account.HasAccess).ShouldBe((0, false));
    }

    [Fact]
    public async Task Revocation_by_the_site_erases_the_token_turns_protection_off_and_says_why()
    {
        var account = await SignedIn();
        var lost = new List<AccountMessage>();
        account.AccessLost += (_, message) => lost.Add(message);
        _api.Status = _ => throw new AccountApiException(AccountFailure.InvalidCredentials, 401);

        await account.CheckAsync(Ct);

        lost.ShouldBe([AccountMessage.Revoked]);
        (account.HasAccess, account.IsSignedIn, account.LastMessage, account.Email).ShouldBe((false, false, AccountMessage.Revoked, null));
        _secrets.Values.ShouldBeEmpty();
        _store.Saved.ShouldBeNull();
        _gateway.Calls.ShouldBe(["drop"], "защита выключается вместе с блоком: иначе в строгом режиме вход не дошел бы до сайта");
        _api.StatusTokens.ShouldBe([Token]);
        Service().HasAccess.ShouldBeFalse("и после перезапуска входа нет");
    }

    [Theory]
    [InlineData(AccountFailure.Network)]
    [InlineData(AccountFailure.Server)]
    public async Task No_network_keeps_access_until_the_confirmed_term_and_then_signs_out(AccountFailure failure)
    {
        var account = await SignedIn();
        var lost = new List<AccountMessage>();
        account.AccessLost += (_, message) => lost.Add(message);
        _api.Status = _ => throw new AccountApiException(failure);
        account.Start();

        _time.Advance(Term - Start - TimeSpan.FromMinutes(1));
        (account.HasAccess, account.IsSignedIn).ShouldBe((true, true), "сеть может быть закрыта блоком при обрыве — это не отзыв");
        _secrets.Values.ShouldContainKey(AccountService.TokenKey);
        _gateway.Calls.ShouldBeEmpty();
        lost.ShouldBeEmpty();

        _time.Advance(TimeSpan.FromMinutes(1));

        lost.ShouldBe([AccountMessage.Expired]);
        (account.HasAccess, account.LastMessage).ShouldBe((false, AccountMessage.Expired));
        _secrets.Values.ShouldBeEmpty();
        _gateway.Calls.ShouldBe(["drop"], "срок прошел без подтверждения — защита выключается, сеть открывается");
    }

    [Fact]
    public async Task Checks_at_once_after_start_then_every_half_an_hour_and_each_answer_extends_the_term()
    {
        await SignedIn();
        _time.Advance(TimeSpan.FromMinutes(5));
        var account = Service();
        account.Start();

        _api.StatusTokens.Count.ShouldBe(1, "сразу при запуске");
        account.ExpiresAt.ShouldBe(_time.GetUtcNow() + TimeSpan.FromDays(30));
        _store.Saved!.CheckedAt.ShouldBe(_time.GetUtcNow());

        _time.Advance(AccountService.Period - TimeSpan.FromMinutes(1));
        _api.StatusTokens.Count.ShouldBe(1);
        _time.Advance(TimeSpan.FromMinutes(1));
        _api.StatusTokens.Count.ShouldBe(2, "раз в 30 минут");

        await account.RefreshAsync(Ct);
        _api.StatusTokens.Count.ShouldBe(2, "показ окна сразу после проверки сайт не дергает");
        _time.Advance(AccountService.RefreshPause);
        await account.RefreshAsync(Ct);
        _api.StatusTokens.Count.ShouldBe(3, "показ окна — повод спросить");
    }

    [Fact]
    public async Task Sign_out_turns_protection_off_tells_the_site_and_forgets_the_token_but_not_the_device()
    {
        var account = await SignedIn();
        var uid = _device.Uid;
        var lost = 0;
        account.AccessLost += (_, _) => lost++;

        await account.SignOutAsync(Ct);

        _gateway.Calls.ShouldBe(["drop"]);
        _api.LogoutTokens.ShouldBe([Token]);
        (_secrets.Values.Count, _store.Saved, account.HasAccess, account.LastMessage).ShouldBe((0, null, false, AccountMessage.None));
        _device.Uid.ShouldBe(uid);
        lost.ShouldBe(0, "выход по просьбе — не потеря доступа");
    }

    [Fact]
    public async Task Sign_out_without_network_still_forgets_the_token()
    {
        var account = await SignedIn();
        _api.Logout = _ => throw new AccountApiException(AccountFailure.Network);

        await account.SignOutAsync(Ct);

        (_secrets.Values.Count, account.HasAccess).ShouldBe((0, false));
    }

    [Fact]
    public async Task Protection_held_by_the_service_without_a_sign_in_is_turned_off_once()
    {
        // Обновление с 0.1: установленная служба подняла защиту при загрузке, а входа еще нет.
        var account = Service();
        account.Start();
        await WaitUntil(() => _gateway.Calls.Count > 0);

        _gateway.Calls.ShouldBe(["drop"], "без доступа защиты нет — и блок снимается, чтобы вход дошел до сайта");

        _gateway.Announce();
        _time.Advance(TimeSpan.FromMinutes(5));
        await Task.Delay(50, Ct);
        _gateway.Calls.ShouldBe(["drop"], "служба не ответила — то же состояние не просить снова на каждом тике");

        // Служба подняла защиту снова (новое состояние) — снова выключить.
        _gateway.Status = _gateway.Status.MoveTo(ConnectionState.Reconnecting, _time.GetUtcNow());
        _gateway.Announce();
        await WaitUntil(() => _gateway.Calls.Count > 1);
        _gateway.Calls.ShouldBe(["drop", "drop"]);
    }

    [Theory]
    [InlineData(ConnectionState.Connected)]
    [InlineData(ConnectionState.Blocked)]
    public async Task Without_a_sign_in_the_first_state_of_the_service_holding_protection_turns_it_off(ConnectionState held)
    {
        var gateway = new ConnectionControllerTests.FakeGateway(ConnectionState.Off) { DropsToOff = true };
        using var account = new AccountService(_api, _store, _device, _secrets, gateway, _time);
        account.Start();
        await Task.Delay(50, Ct);
        gateway.Calls.ShouldBeEmpty("служба еще ничего не прислала — выключать нечего");

        gateway.Status = gateway.Status.MoveTo(ConnectionState.Connecting, _time.GetUtcNow()).MoveTo(held, _time.GetUtcNow());
        gateway.Announce();
        await WaitUntil(() => gateway.Calls.Count > 0);
        gateway.Calls.ShouldBe(["drop"], "сразу по состоянию службы, не дожидаясь тика таймера");
        _time.Advance(TimeSpan.FromMinutes(3));
        await Task.Delay(50, Ct);

        gateway.Calls.ShouldBe(["drop"], "первое состояние службы с защитой — одно выключение, «Выключено» больше не трогается");
        gateway.Status.State.ShouldBe(ConnectionState.Off);
    }

    [Fact]
    public async Task With_valid_access_protection_of_the_service_is_left_alone()
    {
        await SignedIn();
        var account = Service();
        account.Start();
        _gateway.Announce();
        _time.Advance(TimeSpan.FromMinutes(3));
        await Task.Delay(50, Ct);

        account.HasAccess.ShouldBeTrue();
        _gateway.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sign_in_clears_the_old_refusal_of_the_window()
    {
        var account = Service();
        _gateway.Grants.ShouldBe(0);

        await account.SignInAsync("user@example.com", "pw", "123456", null, Ct);

        _gateway.Grants.ShouldBe(1, "отказ PGC-ACC-001 до входа больше не правда");
    }

    [Fact]
    public async Task Unexpected_failure_of_the_site_client_is_an_answer_not_an_exception()
    {
        _api.Login = _ => throw new IOException("обрыв");
        var account = Service();

        (await account.SignInAsync("user@example.com", "pw", "123456", null, Ct)).Failure.ShouldBe(AccountFailure.Network);
        _api.Login = _ => throw new InvalidDataException("что-то другое");
        (await account.SignInAsync("user@example.com", "pw", "123456", null, Ct)).Failure.ShouldBe(AccountFailure.Server);
        account.HasAccess.ShouldBeFalse();

        _api.Login = null;
        await account.SignInAsync("user@example.com", "pw", "123456", null, Ct);
        _api.Status = _ => throw new IOException("обрыв");
        await account.CheckAsync(Ct);
        (account.HasAccess, account.IsChecking).ShouldBe((true, false), "сбой проверки — не отзыв");
    }

    [Fact]
    public async Task Updates_feed_is_asked_with_the_token_for_this_os_and_comes_as_it_is()
    {
        var account = Service();
        (await account.UpdatesFeedAsync(UpdateChannel.Stable, Ct)).ShouldBeNull("входа нет — и ленты нет");
        _api.Updates.ShouldBeEmpty("без входа сайт не спрашивается");

        await account.SignInAsync("user@example.com", "pw", "123456", null, Ct);
        _api.Feed = [9, 8, 7];

        (await account.UpdatesFeedAsync(UpdateChannel.Beta, Ct)).ShouldBe([9, 8, 7]);
        _api.Updates.ShouldBe([(Token, DevicePlatform.Current, UpdateChannel.Beta)]);
        ((IUpdateFeedSource)account).IsSignedIn.ShouldBeTrue();
    }

    [Fact]
    public async Task Updates_feed_401_is_a_revocation_like_the_check_of_access()
    {
        var account = await SignedIn();
        var lost = new List<AccountMessage>();
        account.AccessLost += (_, message) => lost.Add(message);
        _api.UpdatesFailure = AccountFailure.InvalidCredentials;

        (await account.UpdatesFeedAsync(UpdateChannel.Stable, Ct)).ShouldBeNull();

        lost.ShouldBe([AccountMessage.Revoked]);
        (account.IsSignedIn, account.LastMessage).ShouldBe((false, AccountMessage.Revoked));
        _secrets.Values.ShouldBeEmpty();
        _gateway.Calls.ShouldBe(["drop"], "без доступа защиты нет — как при отзыве проверкой");
    }

    [Theory]
    [InlineData(AccountFailure.Network)]
    [InlineData(AccountFailure.Server)]
    [InlineData(AccountFailure.Validation)]
    public async Task Updates_feed_the_site_did_not_give_keeps_the_sign_in(AccountFailure failure)
    {
        var account = await SignedIn();
        _api.UpdatesFailure = failure;

        (await account.UpdatesFeedAsync(UpdateChannel.Stable, Ct)).ShouldBeNull();

        (account.IsSignedIn, account.HasAccess).ShouldBe((true, true), "сеть и сбой сайта — не отзыв");
        _gateway.Calls.ShouldBeEmpty();
        _api.UpdatesFailure = null;
        _api.UpdatesException = new IOException("обрыв");
        (await account.UpdatesFeedAsync(UpdateChannel.Stable, Ct)).ShouldBeNull("непредвиденный сбой клиента сайта — ответ, а не исключение");
        account.IsSignedIn.ShouldBeTrue();
    }

    [Fact]
    public async Task Demo_has_no_updates_feed_and_does_not_ask_the_site()
    {
        var account = Service(new AccountOptions(Required: false));

        (await account.UpdatesFeedAsync(UpdateChannel.Stable, Ct)).ShouldBeNull();

        _api.Updates.ShouldBeEmpty();
    }

    [Fact]
    public void Saved_details_without_a_token_are_no_sign_in()
    {
        _store.Saved = new SavedAccount(Term, "user@example.com", null, "WORKSTATION", Start);

        Service().HasAccess.ShouldBeFalse();

        _store.Saved.ShouldBeNull("сведения без токена ничего не дают");
    }

    [Fact]
    public async Task Demo_needs_no_sign_in_and_never_asks_the_site()
    {
        var account = Service(new AccountOptions(Required: false));
        account.Start();
        _gateway.Announce();
        await Task.Delay(50, Ct);
        _gateway.Calls.ShouldBeEmpty("в демо-режиме защиту без входа не выключают");

        (account.IsRequired, account.HasAccess, account.MayConnect).ShouldBe((false, true, true));
        (await account.SignInAsync("user@example.com", "pw", "123456", null, Ct)).Succeeded.ShouldBeTrue();
        _api.Logins.ShouldBeEmpty();
        _api.StatusTokens.ShouldBeEmpty();
    }

    [Fact]
    public async Task Connect_on_launch_waits_for_sign_in()
    {
        var account = Service();
        var settings = new SettingsService(new MemorySettings());
        settings.Update(s => s with { ConnectOnLaunch = true, OnboardingDone = true });
        var gateway = new ConnectionControllerTests.FakeGateway(ConnectionState.Off, "nl");
        var launch = new LaunchConnect(settings, gateway, new ConnectionControllerTests.FakeCatalog(Nl) { SelectedId = "nl" }, _time, account: account);

        var run = launch.RunAsync(Ct);
        _time.Advance(LaunchConnect.Wait);
        await Task.Delay(50, Ct);

        gateway.Calls.ShouldBeEmpty("без входа подключение при запуске не начинается");
        run.IsCompleted.ShouldBeFalse();

        await account.SignInAsync("user@example.com", "pw", "123456", null, Ct);
        await WaitUntil(() => gateway.Calls.Count > 0 || run.IsCompleted);
        _time.Advance(LaunchConnect.Wait);
        await run;

        gateway.Calls.ShouldBe(["connect:nl"], "вошли — защита включается, как просили");
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(10, Ct);
        }
    }

    private async Task<AccountService> SignedIn()
    {
        var account = Service();
        (await account.SignInAsync("user@example.com", "pw", "123456", null, Ct)).Succeeded.ShouldBeTrue();
        return account;
    }

    private AccountService Service(AccountOptions? options = null)
    {
        var service = new AccountService(_api, _store, _device, _secrets, _gateway, _time, options: options);
        _services.Add(service);
        return service;
    }

    private sealed class FakeApi(TimeProvider time) : IAccountApi
    {
        public List<LoginRequest> Logins { get; } = [];

        public List<string> StatusTokens { get; } = [];

        public List<string> LogoutTokens { get; } = [];

        public Func<LoginRequest, AccountLogin>? Login { get; set; }

        public Func<string, AccountStatus>? Status { get; set; }

        public Func<string, bool>? Logout { get; set; }

        public Task<AccountLogin> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
        {
            Logins.Add(request);
            return Task.FromResult(Login?.Invoke(request) ?? new AccountLogin(Token,
                new AccountStatus(Start + TimeSpan.FromDays(30), new AccountUser(request.Email, "Роман"), new AccountDevice(request.Device.Name))));
        }

        public Task<AccountStatus> StatusAsync(string token, CancellationToken cancellationToken = default)
        {
            StatusTokens.Add(token);
            return Task.FromResult(Status?.Invoke(token) ?? new AccountStatus(time.GetUtcNow() + TimeSpan.FromDays(30),
                new AccountUser("user@example.com", "Роман"), new AccountDevice("WORKSTATION")));
        }

        public Task LogoutAsync(string token, CancellationToken cancellationToken = default)
        {
            LogoutTokens.Add(token);
            Logout?.Invoke(token);
            return Task.CompletedTask;
        }

        /// <summary>Запросы ленты: токен, ОС, канал.</summary>
        public List<(string Token, string Platform, UpdateChannel Channel)> Updates { get; } = [];

        public byte[] Feed { get; set; } = [1];

        /// <summary>Почему сайт не отдал ленту; null — отдал.</summary>
        public AccountFailure? UpdatesFailure { get; set; }

        /// <summary>Сбой, который клиент сайта не перевел в причину; null — нет.</summary>
        public Exception? UpdatesException { get; set; }

        public Task<byte[]> UpdatesAsync(string token, string platform, UpdateChannel channel, CancellationToken cancellationToken = default)
        {
            Updates.Add((token, platform, channel));
            return UpdatesException is { } error ? Task.FromException<byte[]>(error)
                : UpdatesFailure is { } failure ? Task.FromException<byte[]>(new AccountApiException(failure, failure == AccountFailure.InvalidCredentials ? 401 : null))
                : Task.FromResult(Feed);
        }
    }

    private sealed class MemoryAccountStore : IAccountStore
    {
        public SavedAccount? Saved { get; set; }

        /// <summary>База программы отказывает на записи.</summary>
        public bool Fails { get; set; }

        public SavedAccount? Load() => Saved;

        public void Save(SavedAccount account) => Saved = Fails ? throw new InvalidOperationException() : account;

        public void Clear() => Saved = null;
    }

    private sealed class FakeDevice : IDeviceIdentity
    {
        public string Uid { get; } = Guid.NewGuid().ToString();

        public string Name => "WORKSTATION";

        public string AppVersion => "0.2.0";
    }

    private sealed class MemorySettings : ISettingsStore
    {
        private AppSettings _saved = AppSettings.Default;

        public AppSettings Load() => _saved;

        public void Save(AppSettings settings) => _saved = settings;
    }
}
