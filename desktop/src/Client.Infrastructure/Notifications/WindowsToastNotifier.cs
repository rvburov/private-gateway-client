using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using PrivateGateway.Client.Application.Notifications;

namespace PrivateGateway.Client.Infrastructure.Notifications;

/// <summary>
/// Windows 10 и 11: всплывающие уведомления (toast) через WinRT напрямую, без пакета и смены целевой платформы. Программа
/// без установщика называет себя своим AppUserModelID: имя и значок для уведомлений — в HKCU\Software\Classes\AppUserModelId.
/// Важное — со звуком уведомления по умолчанию, обычное — без звука; «Не беспокоить» Windows соблюдает сама. Новое
/// уведомление с той же меткой заменяет прежнее и в Центре уведомлений. Нажатие (кнопка или само уведомление) приходит
/// событием Activated, пока программа работает.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class WindowsToastNotifier : ISystemNotifier
{
    /// <summary>AppUserModelID программы.</summary>
    public const string AppId = "PrivateGateway.Client";

    private const string Group = "pg";

    private readonly string _appId;
    private readonly string _displayName;
    private readonly string? _icon;
    private readonly Dictionary<string, string> _tags = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private ActivationHandler? _handler;
    private bool _registered;

    /// <param name="appId">AppUserModelID (в тестах — свой, чтобы не трогать программу).</param>
    /// <param name="displayName">Имя программы в уведомлении.</param>
    /// <param name="icon">Значок PNG; нет — значок Windows по умолчанию.</param>
    public WindowsToastNotifier(string appId = AppId, string displayName = "Private Gateway", string? icon = null)
    {
        _appId = appId;
        _displayName = displayName;
        _icon = icon;
    }

    public bool IsSupported => true;

    public event EventHandler<NoticeReply>? Replied;

    /// <summary>Ключ реестра с именем и значком для уведомлений.</summary>
    public string RegistryKey => @"Software\Classes\AppUserModelId\" + _appId;

    /// <summary>Чем кончился последний показ: null — Windows уведомление приняла.</summary>
    public Exception? LastError { get; private set; }

    public Task ShowAsync(string tag, NoticeText text, bool important, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        lock (_gate)
        {
            LastError = Attempt(() =>
            {
                Register();
                using var toast = Toast(tag, text, important);
                using var notifier = Notifier();
                notifier.CallPointer(6, toast.Pointer);
            });
        }

        return Task.CompletedTask;
    }

    /// <summary>Собрать уведомление со всем, кроме показа: разметка, метка, обработчик нажатия. Ошибка — что не вышло.</summary>
    public Exception? TryBuild(string tag, NoticeText text, bool important)
    {
        lock (_gate)
        {
            return Attempt(() =>
            {
                using var toast = Toast(tag, text, important);
            });
        }
    }

    public Task CloseAsync(string tag, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _ = Attempt(() =>
            {
                using var manager = WinRt.Factory("Windows.UI.Notifications.ToastNotificationManager", WinRt.IToastNotificationManagerStatics2);
                using var history = new ComRef(manager.CallOut(6));
                using var shortTag = new HString(Short(tag));
                using var group = new HString(Group);
                using var appId = new HString(_appId);
                history.Call3(8, shortTag.Handle, group.Handle, appId.Handle);
            });
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Вызовы WinRT в квартире COM. Уведомления выключены в параметрах Windows, их нет в этой сборке, реестр закрыт — молча:
    /// значок в трее скажет сам.
    /// </summary>
    private static Exception? Attempt(Action action)
    {
        try
        {
            using var apartment = Apartment.Enter();
            action();
            return null;
        }
        catch (Exception e) when (e is WinRtException or DllNotFoundException or EntryPointNotFoundException
            or UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return e;
        }
    }

    private ComRef Toast(string tag, NoticeText text, bool important)
    {
        using var document = XmlDocument(ToastXml.Content(tag, text, important));
        using var factory = WinRt.Factory("Windows.UI.Notifications.ToastNotification", WinRt.IToastNotificationFactory);
        var toast = new ComRef(factory.CallOut(6, document.Pointer));
        try
        {
            using (var toast2 = toast.As(WinRt.IToastNotification2))
            {
                toast2.CallString(6, Short(tag));
                toast2.CallString(8, Group);
            }

            _handler ??= new ActivationHandler(this);
            toast.AddHandler(11, _handler.Pointer);
            return toast;
        }
        catch
        {
            toast.Dispose();
            throw;
        }
    }

    /// <summary>Метка Windows не длиннее 16 знаков: короткая из своей метки.</summary>
    private string Short(string tag)
    {
        if (!_tags.TryGetValue(tag, out var shortTag))
        {
            shortTag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tag)))[..16];
            _tags[tag] = shortTag;
        }

        return shortTag;
    }

    private ComRef Notifier()
    {
        using var manager = WinRt.Factory("Windows.UI.Notifications.ToastNotificationManager", WinRt.IToastNotificationManagerStatics);
        using var appId = new HString(_appId);
        return new ComRef(manager.CallOut(7, appId.Handle));
    }

    private static ComRef XmlDocument(string xml)
    {
        using var className = new HString("Windows.Data.Xml.Dom.XmlDocument");
        WinRt.Check(WinRt.RoActivateInstance(className.Handle, out var instance));
        using var inspectable = new ComRef(instance);
        using (var io = inspectable.As(WinRt.IXmlDocumentIO))
        {
            io.CallString(6, xml);
        }

        return inspectable.As(WinRt.IXmlDocument);
    }

    /// <summary>Имя и значок программы для уведомлений: без них Windows уведомление программы без установщика не покажет.</summary>
    private void Register()
    {
        if (_registered)
        {
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RegistryKey);
        key.SetValue("DisplayName", _displayName);
        if (_icon is not null && File.Exists(_icon))
        {
            key.SetValue("IconUri", _icon);
        }

        _registered = true;
    }

    /// <summary>Убрать имя программы для уведомлений (тесты).</summary>
    public void Unregister() => Registry.CurrentUser.DeleteSubKeyTree(RegistryKey, throwOnMissingSubKey: false);

    private void OnActivated(string arguments)
    {
        if (ToastXml.Reply(arguments) is { } reply)
        {
            Replied?.Invoke(this, reply);
        }
    }

    /// <summary>
    /// Обработчик события Activated — объект COM TypedEventHandler&lt;ToastNotification, object&gt; из своей таблицы методов:
    /// QueryInterface, AddRef, Release, Invoke. Живет, пока жив уведомитель.
    /// </summary>
    private sealed unsafe class ActivationHandler
    {
        // TypedEventHandler<ToastNotification, Object> — IID параметризованного делегата WinRT.
        private static readonly Guid Iid = new("ab54de2d-97d9-5528-b6ad-105afe156530");
        private static readonly Guid IUnknown = new("00000000-0000-0000-C000-000000000046");
        private static readonly Guid IAgileObject = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");
        private static readonly nint* Table = CreateTable();

        public ActivationHandler(WindowsToastNotifier owner)
        {
            var self = (Native*)NativeMemory.Alloc((nuint)sizeof(Native));
            self->Table = (nint)Table;
            self->Owner = GCHandle.ToIntPtr(GCHandle.Alloc(owner));
            self->Count = 1;
            Pointer = (nint)self;
        }

        public nint Pointer { get; }

        private static nint* CreateTable()
        {
            var table = (nint*)NativeMemory.Alloc((nuint)(4 * sizeof(nint)));
            table[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterface;
            table[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRef;
            table[2] = (nint)(delegate* unmanaged<nint, uint>)&Release;
            table[3] = (nint)(delegate* unmanaged<nint, nint, nint, int>)&Invoke;
            return table;
        }

        [UnmanagedCallersOnly]
        private static int QueryInterface(nint self, Guid* iid, nint* result)
        {
            if (*iid == Iid || *iid == IUnknown || *iid == IAgileObject)
            {
                Interlocked.Increment(ref ((Native*)self)->Count);
                *result = self;
                return 0;
            }

            *result = 0;
            return unchecked((int)0x80004002);
        }

        [UnmanagedCallersOnly]
        private static uint AddRef(nint self) => (uint)Interlocked.Increment(ref ((Native*)self)->Count);

        [UnmanagedCallersOnly]
        private static uint Release(nint self)
        {
            var count = Interlocked.Decrement(ref ((Native*)self)->Count);
            if (count == 0)
            {
                GCHandle.FromIntPtr(((Native*)self)->Owner).Free();
                NativeMemory.Free((void*)self);
            }

            return (uint)count;
        }

        [UnmanagedCallersOnly]
        private static int Invoke(nint self, nint sender, nint args)
        {
            try
            {
                if (GCHandle.FromIntPtr(((Native*)self)->Owner).Target is WindowsToastNotifier owner && args != 0)
                {
                    using var activated = new ComRef(WinRt.QueryInterface(args, WinRt.IToastActivatedEventArgs));
                    owner.OnActivated(activated.GetString(6));
                }
            }
            catch (WinRtException)
            {
                // Нажатие без аргументов — нечего делать.
            }

            return 0;
        }

        /// <summary>Объект в памяти: таблица методов, ссылка на уведомитель, счетчик ссылок.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct Native
        {
            public nint Table;
            public nint Owner;
            public int Count;
        }
    }

    /// <summary>Поток в многопоточной квартире COM на время вызова, если он еще ни в какой не входил.</summary>
    private readonly struct Apartment : IDisposable
    {
        private readonly bool _entered;

        private Apartment(bool entered) => _entered = entered;

        public static Apartment Enter() => new(WinRt.CoInitializeEx(0, 0) >= 0);

        public void Dispose()
        {
            if (_entered)
            {
                WinRt.CoUninitialize();
            }
        }
    }
}
