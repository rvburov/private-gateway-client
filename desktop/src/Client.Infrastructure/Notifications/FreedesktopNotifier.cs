using System.Net.Sockets;
using System.Security;
using PrivateGateway.Client.Application.Notifications;
using Tmds.DBus.Protocol;

namespace PrivateGateway.Client.Infrastructure.Notifications;

/// <summary>
/// Linux: уведомления по стандарту freedesktop (org.freedesktop.Notifications) на шине D-Bus сеанса — вид рисует оболочка
/// (GNOME, KDE Plasma и другие). Важное — срочность 1 («обычная») со звуком, остальные — срочность 0 без звука. Срочность 2
/// («критическая») не ставится никогда: GNOME показывает ее и в режиме «Не беспокоить», а его соблюдаем (доска 22). Кнопки —
/// только если сервер уведомлений их умеет (возможность «actions»); нажатие на само уведомление открывает окно.
/// </summary>
public sealed class FreedesktopNotifier : ISystemNotifier, IDisposable
{
    private const string Bus = "org.freedesktop.Notifications";
    private const string ObjectPath = "/org/freedesktop/Notifications";

    /// <summary>Действие нажатия на само уведомление (стандарт freedesktop).</summary>
    private const string DefaultAction = "default";

    /// <summary>Значок из темы оболочки: в темах freedesktop он есть почти везде.</summary>
    private const string Icon = "network-vpn";

    private readonly string _address;
    private readonly string _appName;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, uint> _ids = new(StringComparer.Ordinal);
    private readonly Lock _idsGate = new();
    private readonly List<IDisposable> _watches = [];
    private DBusConnection? _connection;
    private bool _actions;
    private bool _markup;

    /// <param name="address">Адрес шины D-Bus сеанса (в тестах — своей).</param>
    /// <param name="appName">Имя программы в уведомлении.</param>
    public FreedesktopNotifier(string address, string appName = "Private Gateway")
    {
        _address = address;
        _appName = appName;
    }

    /// <summary>Шина сеанса текущего пользователя; ее нет (сеанс без оболочки) — уведомлений нет.</summary>
    public static ISystemNotifier ForSession() =>
        DBusAddress.Session is { Length: > 0 } address ? new FreedesktopNotifier(address) : new NoSystemNotifier();

    public bool IsSupported => true;

    public event EventHandler<NoticeReply>? Replied;

    public async Task ShowAsync(string tag, NoticeText text, bool important, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await ConnectAsync().ConfigureAwait(false);
            uint replaces;
            lock (_idsGate)
            {
                replaces = _ids.GetValueOrDefault(tag);
            }

            var id = await connection.CallMethodAsync(Notify(connection, replaces, text, important), (message, _) => message.GetBodyReader().ReadUInt32(), null)
                .ConfigureAwait(false);
            lock (_idsGate)
            {
                _ids[tag] = id;
            }
        }
        catch (Exception e) when (Broken(e))
        {
            Drop();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CloseAsync(string tag, CancellationToken cancellationToken = default)
    {
        uint id;
        lock (_idsGate)
        {
            if (!_ids.Remove(tag, out id))
            {
                return;
            }
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = await ConnectAsync().ConfigureAwait(false);
            await connection.CallMethodAsync(Close(connection, id)).ConfigureAwait(false);
        }
        catch (Exception e) when (Broken(e))
        {
            // Уведомления уже нет (закрыли, сервер перезапущен) или шины нет — убирать нечего.
            if (e is not DBusErrorReplyException)
            {
                Drop();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        Drop();
        _gate.Dispose();
    }

    private async Task<DBusConnection> ConnectAsync()
    {
        if (_connection is { } existing)
        {
            return existing;
        }

        var connection = new DBusConnection(_address);
        try
        {
            await connection.ConnectAsync().ConfigureAwait(false);
            // Отправитель сигнала — уникальное имя сервера уведомлений, а не org.freedesktop.Notifications: сверка отправителя
            // по известному имени сигнал бы отбросила. Свои уведомления узнаются по номеру.
            _watches.Add(await connection.WatchSignalAsync(null, ObjectPath, Bus, "ActionInvoked",
                (message, _) => { var reader = message.GetBodyReader(); return (reader.ReadUInt32(), reader.ReadString()); },
                (Notification<(uint Id, string Action)> signal) => { if (signal.HasValue) OnAction(signal.Value.Id, signal.Value.Action); },
                ObserverFlags.None, emitOnCapturedContext: false).ConfigureAwait(false));
            _watches.Add(await connection.WatchSignalAsync(null, ObjectPath, Bus, "NotificationClosed",
                (message, _) => message.GetBodyReader().ReadUInt32(),
                (Notification<uint> signal) => { if (signal.HasValue) Forget(signal.Value); },
                ObserverFlags.None, emitOnCapturedContext: false).ConfigureAwait(false));
            var capabilities = await connection.CallMethodAsync(Capabilities(connection), (message, _) => message.GetBodyReader().ReadArrayOfString(), null)
                .ConfigureAwait(false);
            _actions = capabilities.Contains("actions");
            _markup = capabilities.Contains("body-markup");
        }
        catch
        {
            foreach (var watch in _watches)
            {
                watch.Dispose();
            }

            _watches.Clear();
            connection.Dispose();
            throw;
        }

        _connection = connection;
        return connection;
    }

    private MessageBuffer Notify(DBusConnection connection, uint replaces, NoticeText text, bool important)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Bus, ObjectPath, Bus, "Notify", "susssasa{sv}i");
        writer.WriteString(_appName);
        writer.WriteUInt32(replaces);
        writer.WriteString(Icon);
        writer.WriteString(text.Title);
        writer.WriteString(_markup ? SecurityElement.Escape(text.Body) : text.Body);

        // Пары «ключ, надпись»: «default» — нажатие на само уведомление, остальное — кнопки, если сервер их умеет.
        List<string> actions = [DefaultAction, text.Title];
        if (_actions)
        {
            foreach (var button in text.Buttons)
            {
                actions.Add(button.Action.ToString());
                actions.Add(button.Label);
            }
        }

        writer.WriteArray(actions);
        var hints = new Dictionary<string, VariantValue>
        {
            ["urgency"] = VariantValue.Byte(important ? (byte)1 : (byte)0),
            ["category"] = VariantValue.String("network"),
            ["desktop-entry"] = VariantValue.String("private-gateway"),
        };
        if (important)
        {
            hints["sound-name"] = VariantValue.String("dialog-warning");
        }
        else
        {
            hints["suppress-sound"] = VariantValue.Bool(true);
        }

        writer.WriteDictionary(hints);

        // Срок показа — по правилам оболочки.
        writer.WriteInt32(-1);
        return writer.CreateMessage();
    }

    private static MessageBuffer Capabilities(DBusConnection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Bus, ObjectPath, Bus, "GetCapabilities", null);
        return writer.CreateMessage();
    }

    private static MessageBuffer Close(DBusConnection connection, uint id)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(Bus, ObjectPath, Bus, "CloseNotification", "u");
        writer.WriteUInt32(id);
        return writer.CreateMessage();
    }

    private void OnAction(uint id, string action)
    {
        string? tag;
        lock (_idsGate)
        {
            tag = _ids.FirstOrDefault(p => p.Value == id).Key;
        }

        if (tag is null)
        {
            return;
        }

        Replied?.Invoke(this, new NoticeReply(tag, action != DefaultAction && Enum.TryParse<NoticeAction>(action, out var parsed) ? parsed : null));
    }

    private void Forget(uint id)
    {
        lock (_idsGate)
        {
            if (_ids.FirstOrDefault(p => p.Value == id).Key is { } tag)
            {
                _ids.Remove(tag);
            }
        }
    }

    // Шина пропала или сервер уведомлений ответил ошибкой: в следующий раз — новое подключение.
    private void Drop()
    {
        foreach (var watch in _watches)
        {
            watch.Dispose();
        }

        _watches.Clear();
        _connection?.Dispose();
        _connection = null;
        lock (_idsGate)
        {
            _ids.Clear();
        }
    }

    private static bool Broken(Exception e) =>
        e is DBusExceptionBase or IOException or SocketException or ObjectDisposedException or InvalidOperationException or UnauthorizedAccessException;
}
