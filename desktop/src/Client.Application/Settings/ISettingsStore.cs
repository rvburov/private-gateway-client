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
    private AppSettings? _current;

    public AppSettings Current => _current ??= store.Load();

    public event EventHandler<AppSettings>? Changed;

    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        var next = change(Current);
        if (next == Current)
        {
            return;
        }

        store.Save(next);
        _current = next;
        Changed?.Invoke(this, next);
    }
}
