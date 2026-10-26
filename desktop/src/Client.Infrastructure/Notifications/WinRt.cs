using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PrivateGateway.Client.Infrastructure.Notifications;

/// <summary>
/// Минимум WinRT для уведомлений: строки HSTRING, фабрики классов и вызовы методов по номеру в таблице (номера — из
/// windows.ui.notifications.h и windows.data.xml.dom.h; первые шесть — IUnknown и IInspectable).
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class WinRt
{
    public static readonly Guid IXmlDocument = new("f7f3a506-1e87-42d6-bcfb-b8c809fa5494");
    public static readonly Guid IXmlDocumentIO = new("6cd0e74e-ee65-4489-9ebf-ca43e87ba637");
    public static readonly Guid IToastNotificationFactory = new("04124b20-82c6-4229-b109-fd9ed4662b53");
    public static readonly Guid IToastNotification2 = new("9dfb9fd1-143a-490e-90bf-b9fba7132de7");
    public static readonly Guid IToastNotificationManagerStatics = new("50ac103f-d235-4598-bbef-98fe4d1a3ad4");
    public static readonly Guid IToastNotificationManagerStatics2 = new("7ab93c52-0e48-4750-ba9d-1a4113981847");
    public static readonly Guid IToastActivatedEventArgs = new("e3bf92f3-c197-436f-8265-0625824f8dac");

    [LibraryImport("api-ms-win-core-winrt-string-l1-1-0.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int WindowsCreateString(string sourceString, int length, out nint hstring);

    [LibraryImport("api-ms-win-core-winrt-string-l1-1-0.dll")]
    internal static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("api-ms-win-core-winrt-string-l1-1-0.dll")]
    internal static unsafe partial char* WindowsGetStringRawBuffer(nint hstring, out uint length);

    [LibraryImport("api-ms-win-core-winrt-l1-1-0.dll")]
    internal static partial int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);

    [LibraryImport("api-ms-win-core-winrt-l1-1-0.dll")]
    internal static partial int RoActivateInstance(nint activatableClassId, out nint instance);

    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(nint reserved, int coInit);

    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();

    public static void Check(int hresult)
    {
        if (hresult < 0)
        {
            throw new WinRtException(hresult);
        }
    }

    /// <summary>Фабрика (или статические методы) класса WinRT с нужным интерфейсом.</summary>
    public static ComRef Factory(string className, Guid iid)
    {
        using var name = new HString(className);
        Check(RoGetActivationFactory(name.Handle, iid, out var factory));
        return new ComRef(factory);
    }

    public static nint QueryInterface(nint unknown, Guid iid)
    {
        Check(Marshal.QueryInterface(unknown, in iid, out var result));
        return result;
    }
}

/// <summary>Вызов WinRT вернул ошибку: уведомления выключены, их нет в этой сборке Windows, объект не тот.</summary>
public sealed class WinRtException : Exception
{
    public WinRtException()
    {
    }

    public WinRtException(string message)
        : base(message)
    {
    }

    public WinRtException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal WinRtException(int hresult)
        : base($"WinRT: 0x{hresult:X8}") => HResult = hresult;
}

/// <summary>Строка WinRT на время вызова.</summary>
[SupportedOSPlatform("windows")]
internal sealed class HString : IDisposable
{
    public HString(string value)
    {
        WinRt.Check(WinRt.WindowsCreateString(value, value.Length, out var handle));
        Handle = handle;
    }

    public nint Handle { get; }

    public void Dispose() => _ = WinRt.WindowsDeleteString(Handle);
}

/// <summary>Ссылка на объект COM: освобождается при Dispose; методы — по номеру в таблице.</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class ComRef(nint pointer) : IDisposable
{
    public nint Pointer { get; private set; } = pointer;

    private nint Method(int slot) => (*(nint**)Pointer)[slot];

    public ComRef As(Guid iid) => new(WinRt.QueryInterface(Pointer, iid));

    /// <summary>Метод без аргументов, отдающий объект.</summary>
    public nint CallOut(int slot)
    {
        nint result;
        WinRt.Check(((delegate* unmanaged<nint, nint*, int>)Method(slot))(Pointer, &result));
        return result;
    }

    /// <summary>Метод с одним аргументом (объект или строка), отдающий объект.</summary>
    public nint CallOut(int slot, nint argument)
    {
        nint result;
        WinRt.Check(((delegate* unmanaged<nint, nint, nint*, int>)Method(slot))(Pointer, argument, &result));
        return result;
    }

    /// <summary>Метод с одним аргументом — объектом или строкой.</summary>
    public void CallPointer(int slot, nint argument) =>
        WinRt.Check(((delegate* unmanaged<nint, nint, int>)Method(slot))(Pointer, argument));

    public void CallString(int slot, string argument)
    {
        using var value = new HString(argument);
        CallPointer(slot, value.Handle);
    }

    public void Call3(int slot, nint first, nint second, nint third) =>
        WinRt.Check(((delegate* unmanaged<nint, nint, nint, nint, int>)Method(slot))(Pointer, first, second, third));

    /// <summary>Подписка на событие: обработчик и метка подписки (не нужна — уведомление живет недолго).</summary>
    public void AddHandler(int slot, nint handler)
    {
        long token;
        WinRt.Check(((delegate* unmanaged<nint, nint, long*, int>)Method(slot))(Pointer, handler, &token));
    }

    /// <summary>Свойство-строка.</summary>
    public string GetString(int slot)
    {
        nint value;
        WinRt.Check(((delegate* unmanaged<nint, nint*, int>)Method(slot))(Pointer, &value));
        try
        {
            var buffer = WinRt.WindowsGetStringRawBuffer(value, out var length);
            return new string(buffer, 0, (int)length);
        }
        finally
        {
            _ = WinRt.WindowsDeleteString(value);
        }
    }

    public void Dispose()
    {
        if (Pointer != 0)
        {
            Marshal.Release(Pointer);
            Pointer = 0;
        }
    }
}
