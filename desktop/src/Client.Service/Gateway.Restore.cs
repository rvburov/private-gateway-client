using PrivateGateway.Client.Core;

namespace PrivateGateway.Client.Service;

public sealed partial class Gateway
{
    /// <summary>
    /// Явное отключение без сохранения блока, в том числе выход из аккаунта. Каждая часть очистки выполняется
    /// независимо: отказ DNS не оставляет firewall и наоборот. Успех RPC означает завершенную очистку, а не
    /// только остановку ядра. Повтор работает и без туннеля, если первый запрос успел его остановить.
    /// </summary>
    private async Task StopAndRestoreNetworkAsync()
    {
        _blockedSites?.Clear();
        List<Exception> failures = [];
        EndPauseTimer();
        // Намерение отключиться отменяет восстановление старого блока сторожем и подключение после паузы.
        _held = null;
        _holdBlock = false;
        _killSwitchOn = false;
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (IsRestoreFailure(e))
        {
            Failed("Сетевые ядра не остановлены", e);
        }

        Attempt(() => _systemProxy?.ReleaseConfirmed(), "Системный прокси не восстановлен");
        // StopCoreAsync восстанавливает DNS только у существующего туннеля. Здесь нужна также повторная
        // попытка после ошибки и снимок, оставшийся от прошлого запуска службы.
        Attempt(() => _options.SystemDns?.Restore(), "DNS ОС не восстановлен");
        Attempt(() => _options.BootConnect?.Forget(), "Подключение при загрузке не удалено");
        Attempt(() =>
        {
            if (_options.BootBlock is { } path)
            {
                Service.BootBlock.Forget(path);
            }
        }, "Блок при загрузке не удален");
        Attempt(() =>
        {
            if (_options.KillSwitch is { } firewall)
            {
                // Идемпотентно снимаем также неизвестные этому запуску остатки и связанные правила ВМ.
                // Отсутствие первого известного фильтра не доказывает отсутствия остальных.
                firewall.Disable();
                if (firewall.IsEnabled)
                {
                    throw new IOException("ОС по-прежнему сообщает о правилах блока при обрыве");
                }
            }
            else if (_blocking)
            {
                throw new IOException("Нет доступа к оставшемуся блоку при обрыве");
            }

            _blocking = false;
            _blockBroken = false;
        }, "Блок при обрыве не снят");

        _journal.Hide([]);
        _blockedSites?.Clear();
        if (failures.Count > 0)
        {
            var unknown = new ProtectionChecks(CheckState.Warning, CheckState.Warning, CheckState.Warning, CheckState.Warning, null);
            if (Status.State != ConnectionState.Error)
            {
                Move(s => s with { ErrorCode = ErrorCodes.NetworkRestoreFailed }, ConnectionState.Error,
                    after: s => s with { Protection = unknown, StrictBlock = false });
            }
            else
            {
                Update(s => s with { ErrorCode = ErrorCodes.NetworkRestoreFailed, Protection = unknown, StrictBlock = false });
            }

            throw new IOException("Сеть после отключения восстановлена не полностью", new AggregateException(failures));
        }

        if (Status.State != ConnectionState.Off)
        {
            Move(s => s, ConnectionState.Off);
        }
        _journal.Add(JournalLevel.Info, "service", "Защита выключена, настройки сети восстановлены");
        return;

        void Attempt(Action action, string description)
        {
            try
            {
                action();
            }
            catch (Exception e) when (IsRestoreFailure(e))
            {
                Failed(description, e);
            }
        }

        void Failed(string description, Exception error)
        {
            failures.Add(error);
            _journal.Add(JournalLevel.Error, "service", $"{ErrorCodes.NetworkRestoreFailed}: {description}: {error.Message}");
        }
    }

    private static bool IsRestoreFailure(Exception error) => error is IOException or UnauthorizedAccessException
        or InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException
        or System.Text.Json.JsonException;
}
