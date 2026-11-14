using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Application.Account;

/// <summary>
/// Выход из аккаунта — явное разрешение вернуть обычную сеть. Команда доступна и на экране входа;
/// ошибку связи со службой нельзя принять за снятие ее правил. Повтор после сбоя безопасен.
/// </summary>
public sealed class AccountNetworkReset(SettingsService settings, IGatewayClient gateway, IUserProxyController proxy)
{
    private readonly Lock _gate = new();
    private TaskCompletionSource<bool>? _attempt;

    public bool IsSignedOut => settings.Current.AccountSignedOut;

    public bool IsRunning { get; private set; }

    public bool NeedsRecovery { get; private set; } = settings.Current.AccountNetworkResetPending;

    public bool HasRestored { get; private set; }

    public event EventHandler? Changed;

    /// <summary>Несекретная отметка запрещает восстановить прежний токен, если его удаление не удалось.</summary>
    public void MarkSignedOut() => settings.Update(s => Disabled(s, signedOut: true));

    /// <summary>Новый вход снимает отметку выхода, но не отметку незавершенного восстановления сети.</summary>
    public void AcceptSignIn() => settings.Update(s => s with { AccountSignedOut = false });

    public Task<bool> RestoreAsync(bool signedOut = true)
    {
        TaskCompletionSource<bool> attempt;
        lock (_gate)
        {
            if (_attempt is not null)
            {
                return _attempt.Task;
            }

            _attempt = attempt = new(TaskCreationOptions.RunContinuationsAsynchronously);
            IsRunning = true;
            NeedsRecovery = true;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        _ = RestoreCoreAsync(attempt, signedOut);
        return attempt.Task;
    }

    private async Task RestoreCoreAsync(TaskCompletionSource<bool> attempt, bool signedOut)
    {
        var restored = true;
        try
        {
            settings.Update(s => Disabled(s, signedOut));
        }
        catch (Exception)
        {
            // Ошибка записи настроек не отменяет попытку открыть сеть и вернуть прокси пользователя.
            restored = false;
        }

        try
        {
            await gateway.DropProtectionAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            restored = false;
        }

        try
        {
            restored &= proxy.RestoreForSignOut();
        }
        catch (Exception)
        {
            restored = false;
        }

        if (restored)
        {
            try
            {
                settings.Update(s => s with { AccountNetworkResetPending = false });
            }
            catch (Exception)
            {
                restored = false;
            }
        }

        lock (_gate)
        {
            HasRestored = restored;
            NeedsRecovery = !restored;
            IsRunning = false;
            _attempt = null;
        }

        attempt.TrySetResult(restored);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static AppSettings Disabled(AppSettings settings, bool signedOut) => settings with
    {
        AccountSignedOut = signedOut,
        AccountNetworkResetPending = true,
        KillSwitch = false,
        ConnectOnLaunch = false,
        Protection = settings.Protection with { BootProtection = false, StrictMode = false },
        Connection = settings.Connection with { SystemProxy = false, VpnSystemProxy = false },
    };
}
