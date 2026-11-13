using System.Data.Common;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Secrets;
using PrivateGateway.Client.Application.Updates;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Account;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Application.Account;

/// <summary>
/// Вход в аккаунт сайта (<see cref="AccountSite"/>): пользоваться программой можно только с действующим доступом,
/// решает сайт. Токен устройства — только в хранилище секретов ОС (<see cref="TokenKey"/>); без хранилища вход не
/// выполняется. Сведения входа без токена — <see cref="IAccountStore"/>.
/// <para>
/// Проверка (<see cref="CheckAsync"/>) — сразу после <see cref="Start"/>, при показе окна (<see cref="RefreshAsync"/>)
/// и раз в <see cref="Period"/>. Отзывает доступ только ответ 401: токен стирается, защита выключается,
/// <see cref="AccessLost"/> с <see cref="AccountMessage.Revoked"/>. Сеть, срок ожидания и 5xx — не отзыв: окно может
/// быть без сети из-за блока при обрыве, и до <see cref="ExpiresAt"/> действует последний подтвержденный срок.
/// Срок прошел без подтверждения — выход так же, с <see cref="AccountMessage.Expired"/>.
/// </para>
/// <para>
/// Без доступа защиты нет (решение 634): выход, отзыв и истечение выключают ее вместе с блоком при обрыве и строгим
/// режимом (<see cref="IGatewayClient.DropProtectionAsync"/>) — иначе вход не дошел бы до сайта. Защиту, которую
/// установленная служба держит без окна (защита при загрузке после обновления, токен не прочитан, выход без связи со
/// службой), окно выключает так же: после <see cref="Start"/> — при каждом новом состоянии службы и на каждом тике
/// таймера, но одно и то же состояние — один раз.
/// </para>
/// <para>
/// Подключение без доступа не начинается (<see cref="MayConnect"/>, код PGC-ACC-001 ставит клиент службы).
/// В демо-режиме (<see cref="AccountOptions.Required"/> = false) вход не нужен и доступ есть всегда.
/// События — в контексте того, кто вызвал (окно); таймер работает в контексте вызова <see cref="Start"/>.
/// </para>
/// <para>
/// Ленту обновлений сайта (<see cref="UpdatesFeedAsync"/>) берет окно этим же входом и передает службе: у службы токена
/// нет. 401 на ленту — тоже отзыв доступа.
/// </para>
/// </summary>
public sealed partial class AccountService : IDisposable, IUpdateFeedSource
{
    /// <summary>Ключ токена в хранилище секретов ОС.</summary>
    public const string TokenKey = "account:token";

    /// <summary>Как часто спрашивать сайт, пока программа работает.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromMinutes(30);

    /// <summary>Чаще этого <see cref="RefreshAsync"/> (показ окна) сайт не спрашивает.</summary>
    public static readonly TimeSpan RefreshPause = TimeSpan.FromMinutes(1);

    // Как часто смотреть, не пора ли: после сна машины проверка и истечение срока не ждут полный период.
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    private readonly IAccountApi _api;
    private readonly IAccountStore _store;
    private readonly IDeviceIdentity _device;
    private readonly ISecretStore _secrets;
    private readonly IGatewayClient _gateway;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private SynchronizationContext? _context;
    private ITimer? _timer;
    private string? _token;
    private SavedAccount? _saved;
    private DateTimeOffset? _attempted;

    // Состояние службы, которое уже просили выключить: не просить снова, пока служба его не сменила.
    private (ConnectionState State, DateTimeOffset Since)? _dropped;

    public AccountService(IAccountApi api, IAccountStore store, IDeviceIdentity device, ISecretStore secrets, IGatewayClient gateway,
        TimeProvider time, ILogger<AccountService>? logger = null, AccountOptions? options = null)
    {
        _api = api;
        _store = store;
        _device = device;
        _secrets = secrets;
        _gateway = gateway;
        _time = time;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        IsRequired = options?.Required ?? true;
        if (IsRequired)
        {
            Load();
        }
    }

    /// <summary>Нужен ли вход; false — демо-режим, доступ есть всегда.</summary>
    public bool IsRequired { get; }

    /// <summary>Вход сохранен (токен в хранилище ОС) — даже если срок уже прошел, но выход еще не случился.</summary>
    public bool IsSignedIn => _token is not null;

    /// <summary>Доступ действует: вход сохранен и подтвержденный срок не прошел (или вход не нужен).</summary>
    public bool HasAccess => !IsRequired || (_token is not null && _saved is { } saved && _time.GetUtcNow() < saved.ExpiresAt);

    /// <summary>Можно ли начинать подключение: то же, что <see cref="HasAccess"/>.</summary>
    public bool MayConnect => HasAccess;

    /// <summary>Почта участника; null — входа нет.</summary>
    public string? Email => _saved?.Email;

    /// <summary>Имя участника на сайте; null — входа нет или имя не задано.</summary>
    public string? DisplayName => _saved?.DisplayName;

    /// <summary>Имя этого устройства на сайте; null — входа нет.</summary>
    public string? DeviceName => _saved?.DeviceName;

    /// <summary>До какого времени действует вход без подтверждения сайта; null — входа нет.</summary>
    public DateTimeOffset? ExpiresAt => _saved?.ExpiresAt;

    /// <summary>Когда сайт подтвердил доступ последний раз; null — входа нет.</summary>
    public DateTimeOffset? CheckedAt => _saved?.CheckedAt;

    /// <summary>Почему показан экран входа: доступ отозван, срок истек или ничего (первый вход, выход сам).</summary>
    public AccountMessage LastMessage { get; private set; }

    /// <summary>Идет проверка доступа.</summary>
    public bool IsChecking { get; private set; }

    /// <summary>Вошли, вышли, пришел ответ проверки, доступ потерян. В контексте окна.</summary>
    public event EventHandler? Changed;

    /// <summary>Доступ потерян без участия пользователя (отозван или истек): токен стерт, защита выключается.</summary>
    public event EventHandler<AccountMessage>? AccessLost;

    /// <summary>Проверять сразу и потом раз в <see cref="Period"/>; работа — в контексте вызова (окно).</summary>
    public void Start()
    {
        if (!IsRequired || _timer is not null)
        {
            return;
        }

        _context = SynchronizationContext.Current;
        _gateway.StatusChanged += OnGatewayStatus;
        _timer = _time.CreateTimer(_ => Post(RunAsync), null, TimeSpan.Zero, Tick);
    }

    public void Dispose()
    {
        _gateway.StatusChanged -= OnGatewayStatus;
        _timer?.Dispose();
    }

    /// <summary>
    /// Войти: почта, пароль и не больше одного из кода 2FA и резервного кода. Первый шаг — без кода: участнику его
    /// хватает, а администратору сайт ответит <see cref="AccountFailure.CodeRequired"/>, и экран входа спросит код и
    /// повторит вход с ним. Хранилище секретов ОС недоступно — отказ <see cref="AccountFailure.Store"/> без запроса к
    /// сайту: токен без защищенного хранилища не держим.
    /// </summary>
    public async Task<LoginOutcome> SignInAsync(string email, string password, string? code, string? recoveryCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(email);
        ArgumentNullException.ThrowIfNull(password);
        if (!IsRequired)
        {
            return LoginOutcome.Success;
        }

        DeviceInfo device;
        try
        {
            if (!_secrets.IsAvailable)
            {
                LogStoreUnavailable(_logger, "нет хранилища");
                return LoginOutcome.Failed(AccountFailure.Store);
            }

            device = new DeviceInfo(_device.Uid, _device.Name, DevicePlatform.Current, _device.AppVersion);
        }
        catch (Exception e) when (StoreFailure(e))
        {
            LogStoreUnavailable(_logger, e.GetType().Name);
            return LoginOutcome.Failed(AccountFailure.Store);
        }

        AccountLogin login;
        try
        {
            login = await _api.LoginAsync(new LoginRequest(email.Trim(), password, Blank(code), Blank(recoveryCode), device), cancellationToken)
                .ConfigureAwait(true);
        }
        catch (AccountApiException e)
        {
            LogSignInFailed(_logger, e.Failure, e.StatusCode);
            return LoginOutcome.Failed(e.Failure, e.RetryAfter);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Клиент сайта переводит сбои в причины; что-то другое — не ронять окно, а сказать «нет связи» или «сбой».
            var failure = e is IOException ? AccountFailure.Network : AccountFailure.Server;
            LogSignInFailed(_logger, failure, null);
            LogUnexpected(_logger, e.GetType().Name);
            return LoginOutcome.Failed(failure);
        }

        var now = _time.GetUtcNow();
        var saved = new SavedAccount(login.Status.ExpiresAt, login.Status.User.Email, login.Status.User.DisplayName, login.Status.Device.Name, now);
        try
        {
            _secrets.Save(TokenKey, login.Token);
            _store.Save(saved);
        }
        catch (Exception e) when (StoreFailure(e))
        {
            // Половина входа хуже никакого: следующий запуск не должен найти токен без сведений или наоборот.
            LogStoreUnavailable(_logger, e.GetType().Name);
            ForgetStored();
            return LoginOutcome.Failed(AccountFailure.Store);
        }

        (_token, _saved, _attempted, _dropped, LastMessage) = (login.Token, saved, now, null, AccountMessage.None);
        LogSignedIn(_logger);
        // Отказ PGC-ACC-001, показанный до входа, больше не правда — снять его до того, как окно уйдет на главную.
        _gateway.AccessGranted();
        Changed?.Invoke(this, EventArgs.Empty);
        return LoginOutcome.Success;
    }

    /// <summary>
    /// Выйти: выключить защиту вместе с блоком (и строгого режима), забыть вход (токен и сведения; uid устройства
    /// остается) и сказать сайту (<c>logout</c>; сеть недоступна — не важно, токен здесь уже стерт).
    /// </summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRequired)
        {
            return;
        }

        var token = _token;
        await DropAsync().ConfigureAwait(true);
        Forget();
        LastMessage = AccountMessage.None;
        LogSignedOut(_logger);
        Changed?.Invoke(this, EventArgs.Empty);
        if (token is null)
        {
            return;
        }

        try
        {
            await _api.LogoutAsync(token, cancellationToken).ConfigureAwait(true);
        }
        catch (AccountApiException e)
        {
            LogLogoutFailed(_logger, e.Failure, e.StatusCode);
        }
    }

    /// <summary>Спросить сайт сейчас, действует ли доступ; уже спрашиваем или входа нет — ничего.</summary>
    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRequired || IsChecking || _token is not { } token)
        {
            return;
        }

        IsChecking = true;
        _attempted = _time.GetUtcNow();
        Changed?.Invoke(this, EventArgs.Empty);
        var revoked = false;
        try
        {
            var status = await _api.StatusAsync(token, cancellationToken).ConfigureAwait(true);
            if (_token == token)
            {
                Confirm(status);
            }
        }
        catch (AccountApiException e) when (e.Failure == AccountFailure.InvalidCredentials)
        {
            revoked = _token == token;
        }
        catch (AccountApiException e)
        {
            // Нет связи или сайт сбоит — не отзыв: действует последний подтвержденный срок.
            LogCheckFailed(_logger, e.Failure, e.StatusCode);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Непредвиденный сбой клиента сайта — тоже не отзыв; срок истечения проверяется ниже как обычно.
            LogUnexpected(_logger, e.GetType().Name);
        }
        finally
        {
            IsChecking = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        if (revoked)
        {
            LogRevoked(_logger);
            await LoseAsync(AccountMessage.Revoked).ConfigureAwait(true);
            return;
        }

        await ExpireIfPassedAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Лента обновлений сайта канала <paramref name="channel"/> для этой ОС — байты ответа как есть: подписи манифестов
    /// проверяет служба. Вход не нужен (демо) или его нет — null без запроса к сайту. 401 — доступ отозван, как у
    /// проверки доступа: вход стирается, защита выключается. Нет связи или сайт сбоит — null, вход остается.
    /// </summary>
    public async Task<byte[]?> UpdatesFeedAsync(UpdateChannel channel, CancellationToken cancellationToken = default)
    {
        if (!IsRequired || _token is not { } token)
        {
            return null;
        }

        try
        {
            return await _api.UpdatesAsync(token, DevicePlatform.Current, channel, cancellationToken).ConfigureAwait(true);
        }
        catch (AccountApiException e) when (e.Failure == AccountFailure.InvalidCredentials)
        {
            if (_token == token)
            {
                LogRevoked(_logger);
                await LoseAsync(AccountMessage.Revoked).ConfigureAwait(true);
            }

            return null;
        }
        catch (AccountApiException e)
        {
            LogUpdatesFailed(_logger, e.Failure, e.StatusCode);
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogUnexpected(_logger, e.GetType().Name);
            return null;
        }
    }

    /// <summary>Показ окна: спросить сайт, если с прошлой попытки прошло не меньше <see cref="RefreshPause"/>.</summary>
    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        _attempted is { } last && _time.GetUtcNow() - last < RefreshPause ? ExpireIfPassedAsync() : CheckAsync(cancellationToken);

    /// <summary>Дождаться доступа: он уже есть или пользователь вошел (<see cref="Changed"/>).</summary>
    public async Task WaitForAccessAsync(CancellationToken cancellationToken = default)
    {
        if (MayConnect)
        {
            return;
        }

        var access = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? sender, EventArgs e)
        {
            if (MayConnect)
            {
                access.TrySetResult();
            }
        }

        Changed += OnChanged;
        try
        {
            if (!MayConnect)
            {
                await access.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Changed -= OnChanged;
        }
    }

    private void Load()
    {
        try
        {
            if (_store.Load() is not { } saved)
            {
                return;
            }

            if (_secrets.Read(TokenKey) is not { Length: > 0 } token)
            {
                // Сведения без токена ничего не дают: начать с чистого входа.
                _store.Clear();
                return;
            }

            (_token, _saved) = (token, saved);
        }
        catch (Exception e) when (StoreFailure(e))
        {
            // Токен не прочитать — входа нет; сведения остаются: хранилище может вернуться к следующему запуску.
            LogStoreUnavailable(_logger, e.GetType().Name);
            (_token, _saved) = (null, null);
        }
    }

    private void Confirm(AccountStatus status)
    {
        _saved = new SavedAccount(status.ExpiresAt, status.User.Email, status.User.DisplayName, status.Device.Name, _time.GetUtcNow());
        try
        {
            _store.Save(_saved);
        }
        catch (Exception e) when (StoreFailure(e))
        {
            // Срок в памяти продлен; не записался — после перезапуска действует прежний, и проверка его продлит.
            LogStoreUnavailable(_logger, e.GetType().Name);
        }
    }

    private async Task ExpireIfPassedAsync()
    {
        if (IsRequired && _token is not null && _saved is { } saved && _time.GetUtcNow() >= saved.ExpiresAt)
        {
            LogExpired(_logger);
            await LoseAsync(AccountMessage.Expired).ConfigureAwait(true);
        }
    }

    // Доступ потерян: забыть вход, показать экран входа с причиной и выключить защиту вместе с блоком.
    private async Task LoseAsync(AccountMessage message)
    {
        Forget();
        LastMessage = message;
        Changed?.Invoke(this, EventArgs.Empty);
        AccessLost?.Invoke(this, message);
        await DropAsync().ConfigureAwait(true);
    }

    // Доступа нет, а служба держит защиту или блок: выключить и открыть сеть. Одно состояние службы — одна просьба:
    // служба не ответила — просить снова, когда она пришлет новое состояние, а не на каждом тике.
    private async Task HoldOffAsync()
    {
        var status = _gateway.Status;
        if (HasAccess || ConnectionController.NetworkIsOpen(status) || _dropped == (status.State, status.Since))
        {
            return;
        }

        LogProtectionWithoutAccess(_logger, status.State);
        await DropAsync().ConfigureAwait(true);
    }

    private void Forget()
    {
        (_token, _saved, _attempted) = (null, null, null);
        ForgetStored();
    }

    private void ForgetStored()
    {
        try
        {
            _secrets.Delete(TokenKey);
        }
        catch (Exception e) when (StoreFailure(e))
        {
            LogStoreUnavailable(_logger, e.GetType().Name);
        }

        try
        {
            _store.Clear();
        }
        catch (Exception e) when (StoreFailure(e))
        {
            LogStoreUnavailable(_logger, e.GetType().Name);
        }
    }

    // Выключить защиту и открыть сеть при любом пути службы: установленная могла держать ее без окна, а строгий режим
    // оставил бы сеть закрытой, и вход не дошел бы до сайта.
    private async Task DropAsync()
    {
        var status = _gateway.Status;
        _dropped = (status.State, status.Since);
        try
        {
            await _gateway.DropProtectionAsync().ConfigureAwait(true);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or TimeoutException)
        {
            LogDisconnectFailed(_logger, e.GetType().Name);
        }
    }

    private void Post(Func<Task> work)
    {
        if (_context is { } context)
        {
            context.Post(_ => _ = work(), null);
        }
        else
        {
            _ = work();
        }
    }

    // Новое состояние службы — из любого потока; решать в контексте окна, как и все остальное.
    private void OnGatewayStatus(object? sender, ConnectionStatus status)
    {
        if (!HasAccess)
        {
            Post(HoldOffAsync);
        }
    }

    private async Task RunAsync()
    {
        await ExpireIfPassedAsync().ConfigureAwait(true);
        if (_token is not null && (_attempted is not { } last || _time.GetUtcNow() - last >= Period))
        {
            await CheckAsync().ConfigureAwait(true);
        }

        await HoldOffAsync().ConfigureAwait(true);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool StoreFailure(Exception error) => error is InvalidOperationException or IOException or UnauthorizedAccessException
        or SecurityException or NotSupportedException or FormatException or CryptographicException or DbException
        or ExternalException or DllNotFoundException or EntryPointNotFoundException;

    [LoggerMessage(Level = LogLevel.Information, Message = "Аккаунт: вход выполнен")]
    private static partial void LogSignedIn(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Аккаунт: выход")]
    private static partial void LogSignedOut(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Аккаунт: вход не выполнен: {Failure} {StatusCode}")]
    private static partial void LogSignInFailed(ILogger logger, AccountFailure failure, int? statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Аккаунт: проверка не прошла, действует прежний срок: {Failure} {StatusCode}")]
    private static partial void LogCheckFailed(ILogger logger, AccountFailure failure, int? statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Аккаунт: лента обновлений сайта не получена: {Failure} {StatusCode}")]
    private static partial void LogUpdatesFailed(ILogger logger, AccountFailure failure, int? statusCode);

    [LoggerMessage(Level = LogLevel.Information, Message = "Аккаунт: сайт не принял выход: {Failure} {StatusCode}; вход здесь стерт")]
    private static partial void LogLogoutFailed(ILogger logger, AccountFailure failure, int? statusCode);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Аккаунт: доступ отозван на сайте — вход стерт, защита выключается")]
    private static partial void LogRevoked(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Аккаунт: срок входа истек без подтверждения сайта — вход стерт, защита выключается")]
    private static partial void LogExpired(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Аккаунт: хранилище секретов или база недоступны: {Reason}")]
    private static partial void LogStoreUnavailable(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Аккаунт: защиту выключить не удалось: {Reason}")]
    private static partial void LogDisconnectFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Аккаунт: доступа нет, а служба держит защиту ({State}) — защита выключается, сеть открывается")]
    private static partial void LogProtectionWithoutAccess(ILogger logger, ConnectionState state);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Аккаунт: непредвиденный сбой клиента сайта: {Reason}")]
    private static partial void LogUnexpected(ILogger logger, string reason);
}
