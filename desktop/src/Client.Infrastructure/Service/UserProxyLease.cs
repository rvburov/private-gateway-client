using System.Security;
using System.Text.Json;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Infrastructure.Service;

/// <summary>Снимок до включения и точные настройки, которыми владеет окно. Оба сохраняются для восстановления после сбоя.</summary>
public sealed record UserProxyBackup(SystemProxySnapshot Original, SystemProxySnapshot Applied, int Port,
    bool AppliedConfirmed = false, SystemProxySnapshot? BeforeApply = null, bool Restoring = false,
    ServiceKind OwnerService = ServiceKind.Installed);

public interface IUserProxyBackupStore
{
    UserProxyBackup? Read();
    void Write(UserProxyBackup snapshot);
    void Delete();
}

/// <summary>Замена файла атомарна; исходный снимок сохраняется прежде, чем меняются настройки ОС.</summary>
public sealed class FileUserProxyBackupStore(string path) : IUserProxyBackupStore
{
    public UserProxyBackup? Read()
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var snapshot = JsonSerializer.Deserialize<UserProxyBackup>(File.ReadAllText(path));
        return snapshot is { Original.Values: not null, Applied.Values: not null, Port: > 0 and <= 65535 }
            && (snapshot.BeforeApply is null || snapshot.BeforeApply.Values is not null)
            && snapshot.OwnerService is ServiceKind.Installed or ServiceKind.User
            ? snapshot : throw new JsonException("Поврежденный снимок системного прокси");
    }

    /// <summary>После аварии окна служба пользователя завершилась с родителем: ее можно поднять ради достоверного Off.</summary>
    public bool IsUserServiceBackup()
    {
        try
        {
            return Read()?.OwnerService == ServiceKind.User;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or JsonException)
        {
            return false;
        }
    }

    public void Write(UserProxyBackup snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot));
        File.Move(temporary, path, overwrite: true);
    }

    public void Delete() => File.Delete(path);
}

/// <summary>
/// Настройки ОС принадлежат окну только пока они точно равны сохраненному Applied. Другая программа или пользователь
/// изменили хотя бы одно поле — снимок больше нельзя возвращать поверх них, в том числе после аварии окна.
/// </summary>
public sealed class UserProxyLease(ISystemProxy proxy, IUserProxyBackupStore store)
{
    private UserProxyBackup? _backup;
    private bool _loaded;

    public int? Port => Backup?.Port;

    public bool IsOwned => Backup is { Restoring: false } snapshot && Same(proxy.Read(), snapshot.Applied);

    public bool WasChanged => Backup is { } snapshot && !Recoverable(snapshot, proxy.Read());

    /// <summary>Чужие изменения не перезаписываются даже при очередном Connected или повторном запуске окна.</summary>
    public bool Apply(int port, ServiceKind ownerService = ServiceKind.Installed)
    {
        var current = proxy.Read();
        if (Backup is { } previous && !Recoverable(previous, current))
        {
            Forget();
            return false;
        }

        if (Backup is { Port: var active, Restoring: false } saved && active == port && Same(current, saved.Applied))
        {
            if (!saved.AppliedConfirmed || saved.OwnerService != ownerService)
            {
                var confirmed = saved with { AppliedConfirmed = true, OwnerService = ownerService };
                store.Write(confirmed);
                _backup = confirmed;
            }

            return true;
        }

        var original = Backup?.Original ?? proxy.Read();
        // Ожидаемый снимок нужен до Enable: окно может аварийно завершиться сразу после записи в HKCU или gsettings.
        var expected = proxy.Expected(port) ?? throw new InvalidOperationException($"Прокси «{proxy.Name}» не сообщает, что запишет");
        var next = new UserProxyBackup(original, expected, port, BeforeApply: current, OwnerService: ownerService);
        store.Write(next);
        _backup = next;
        try
        {
            proxy.Enable(port);
        }
        catch
        {
            // Ошибка в середине записи HKCU/WinINet не равна чужому изменению: вернуть состояние до этой попытки,
            // только если каждое поле еще относится к сохраненному переходу. Исходный снимок остается для повторения.
            var after = proxy.Read();
            if (!Same(after, current) && Recoverable(next, after))
            {
                proxy.Restore(current);
            }

            throw;
        }
        if (!Same(proxy.Read(), expected))
        {
            throw new IOException("Настройки системного прокси не подтверждены после применения");
        }

        var applied = next with { AppliedConfirmed = true };
        store.Write(applied);
        _backup = applied;

        return true;
    }

    /// <summary>true — прежние настройки возвращены; false — снимка нет или настройки уже принадлежат другому.</summary>
    public bool Release()
    {
        if (Backup is not { } snapshot)
        {
            return false;
        }

        var current = proxy.Read();
        if (!Recoverable(snapshot, current))
        {
            Forget();
            return false;
        }

        // Возврат тоже может оборваться между полями HKCU и уведомлением WinINet.
        var restoring = snapshot with { AppliedConfirmed = false, BeforeApply = current, Restoring = true };
        store.Write(restoring);
        _backup = restoring;
        proxy.Restore(snapshot.Original);
        if (!Same(proxy.Read(), snapshot.Original))
        {
            throw new IOException("Возврат системного прокси не подтвержден");
        }

        Forget();
        return true;
    }

    private UserProxyBackup? Backup
    {
        get
        {
            if (!_loaded)
            {
                _backup = store.Read();
                _loaded = true;
            }

            return _backup;
        }
    }

    private void Forget()
    {
        store.Delete();
        _backup = null;
    }

    public static bool Same(SystemProxySnapshot left, SystemProxySnapshot right) => left.Values.Count == right.Values.Count
        && left.Values.All(v => right.Values.TryGetValue(v.Key, out var value) && value == v.Value);

    private static bool Recoverable(UserProxyBackup snapshot, SystemProxySnapshot current)
    {
        if (snapshot.Restoring)
        {
            return Transition(snapshot.BeforeApply ?? snapshot.Applied, snapshot.Original, current);
        }

        if (Same(current, snapshot.Applied))
        {
            return true;
        }

        if (snapshot.AppliedConfirmed)
        {
            return false;
        }

        return Transition(snapshot.BeforeApply ?? snapshot.Original, snapshot.Applied, current);
    }

    private static bool Transition(SystemProxySnapshot before, SystemProxySnapshot after, SystemProxySnapshot current) =>
        current.Values.Keys.All(k => before.Values.ContainsKey(k) || after.Values.ContainsKey(k))
            && before.Values.Keys.Concat(after.Values.Keys).Distinct(StringComparer.Ordinal).All(k =>
                current.Values.GetValueOrDefault(k) == before.Values.GetValueOrDefault(k)
                || current.Values.GetValueOrDefault(k) == after.Values.GetValueOrDefault(k));
}
