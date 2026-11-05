using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Application.Settings;

/// <summary>Где лежат настройки (SQLite в папке данных пользователя).</summary>
public interface ISettingsStore
{
    AppSettings Load();

    void Save(AppSettings settings);
}

/// <summary>Текущие настройки программы: читаются при запуске, сохраняются при каждом изменении.</summary>
public sealed class SettingsService(ISettingsStore store)
{
    private readonly Lock _gate = new();
    private AppSettings? _current;

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current ??= store.Load();
            }
        }
    }

    public event EventHandler<AppSettings>? Changed;

    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        AppSettings next;
        lock (_gate)
        {
            var current = _current ??= store.Load();
            next = change(current);
            if (next == current)
            {
                return;
            }

            store.Save(next);
            _current = next;
        }

        // Подписчик может читать защищенное хранилище: не удерживать здесь замок настроек в обратном порядке.
        Changed?.Invoke(this, next);
    }
}
