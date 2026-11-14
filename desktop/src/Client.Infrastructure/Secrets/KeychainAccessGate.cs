namespace PrivateGateway.Client.Infrastructure.Secrets;

/// <summary>
/// Разрешение системных окон Keychain общее для процесса. Все нативные обращения идут через один экземпляр:
/// фоновая уборка не отключит запрос пароля у параллельного чтения, а прежнее значение восстанавливается даже при сбое.
/// </summary>
public sealed class KeychainAccessGate(Func<bool> interactionAllowed, Action<bool> setInteractionAllowed)
{
    private readonly Lock _gate = new();
    private bool? _restorePending;

    public T Run<T>(Func<T> action)
    {
        lock (_gate)
        {
            RestorePending();
            return action();
        }
    }

    public void Run(Action action)
    {
        lock (_gate)
        {
            RestorePending();
            action();
        }
    }

    public T WithoutInteraction<T>(Func<T> action)
    {
        lock (_gate)
        {
            RestorePending();
            var allowed = interactionAllowed();
            _restorePending = allowed;
            try
            {
                setInteractionAllowed(false);
                return action();
            }
            finally
            {
                RestorePending();
            }
        }
    }

    private void RestorePending()
    {
        if (_restorePending is { } allowed)
        {
            // Если сама ОС отказала при восстановлении флага, следующая операция сначала повторит восстановление.
            // Ни чтение ключа, ни новая уборка не работают с неизвестным режимом системных окон.
            setInteractionAllowed(allowed);
            _restorePending = null;
        }
    }
}
