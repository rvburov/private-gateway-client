using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.UI.Services;

/// <summary>
/// Значок строки меню macOS. Avalonia привязывает к нему меню AppKit и не получает события кнопок мыши.
/// Здесь AppKit показывает то же меню по левому нажатию, а по правому открывается окно.
/// </summary>
internal sealed class MacStatusItem : IDisposable
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const int RightMouseUp = 4;
    private const int MouseUpMask = (1 << 2) | (1 << RightMouseUp);
    private static readonly NativeAction ClickHandler = OnClick;
    private static readonly NativeAction SelectHandler = OnSelect;
    private static MacStatusItem? _current;

    private readonly TrayViewModel _tray;
    private readonly Action _show;
    private readonly nint _bar;
    private readonly nint _item;
    private readonly nint _button;
    private readonly nint _target;
    private NativeMenuItemBase[] _items = [];
    private (Tone, string)? _image;
    private bool _disposed;

    public MacStatusItem(TrayViewModel tray, Action show)
    {
        _tray = tray;
        _show = show;
        _bar = Send(Class("NSStatusBar"), Selector("systemStatusBar"));
        _item = SendDouble(_bar, Selector("statusItemWithLength:"), -2);
        _button = Send(_item, Selector("button"));
        _target = Send(TargetClass(), Selector("new"));
        _current = this;
        SendPointer(_button, Selector("setTarget:"), _target);
        SendPointer(_button, Selector("setAction:"), Selector("pgStatusClick:"));
        SendPointer(_button, Selector("sendActionOn:"), MouseUpMask);
        _tray.PropertyChanged += OnTrayChanged;
        Update();
    }

    // AppKit разрешает создать NSStatusItem только на главном потоке ОС. У Avalonia Headless свой поток.
    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "pthread_main_np")]
    private static extern int OnMainThread();

    public static bool IsMainThread() => OnMainThread() != 0;

    private static nint TargetClass()
    {
        const string name = "PrivateGatewayStatusTarget";
        var type = Class(name);
        if (type != 0)
        {
            return type;
        }

        type = AllocateClass(Class("NSObject"), Utf8(name), 0);
        AddMethod(type, Selector("pgStatusClick:"), ClickHandler, Utf8("v@:@"));
        AddMethod(type, Selector("pgStatusSelect:"), SelectHandler, Utf8("v@:@"));
        RegisterClass(type);
        return type;
    }

    private static void OnClick(nint self, nint command, nint sender)
    {
        if (_current is not { } current)
        {
            return;
        }

        var application = Send(Class("NSApplication"), Selector("sharedApplication"));
        var mouse = Send(application, Selector("currentEvent"));
        if (Send(mouse, Selector("type")) == RightMouseUp)
        {
            Dispatcher.UIThread.Post(current._show);
        }
        else
        {
            current.OpenMenu();
        }
    }

    private static void OnSelect(nint self, nint command, nint sender)
    {
        if (_current is not { } current)
        {
            return;
        }

        var tag = (int)Send(sender, Selector("tag"));
        if ((uint)tag < (uint)current._items.Length && current._items[tag] is NativeMenuItem { Command: { } action } item)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (action.CanExecute(item.CommandParameter))
                {
                    action.Execute(item.CommandParameter);
                }
            });
        }
    }

    private void OpenMenu()
    {
        _items = [.. _tray.Menu.Items];
        var menu = Send(Class("NSMenu"), Selector("new"));
        SendPointer(menu, Selector("setAutoenablesItems:"), 0);
        try
        {
            for (var i = 0; i < _items.Length; i++)
            {
                if (_items[i] is NativeMenuItem item)
                {
                    var row = Send(Class("NSMenuItem"), Selector("new"));
                    SendPointer(row, Selector("setTitle:"), String(item.Header ?? string.Empty));
                    SendPointer(row, Selector("setTarget:"), _target);
                    SendPointer(row, Selector("setAction:"), Selector("pgStatusSelect:"));
                    SendPointer(row, Selector("setTag:"), i);
                    SendPointer(row, Selector("setState:"), item.IsChecked ? 1 : 0);
                    SendPointer(row, Selector("setEnabled:"),
                        item.IsEnabled && item.Command?.CanExecute(item.CommandParameter) == true ? 1 : 0);
                    if (item.ToolTip is { } tooltip)
                    {
                        SendPointer(row, Selector("setToolTip:"), String(tooltip));
                    }

                    SendPointer(menu, Selector("addItem:"), row);
                    Send(row, Selector("release"));
                }
                else
                {
                    SendPointer(menu, Selector("addItem:"), Send(Class("NSMenuItem"), Selector("separatorItem")));
                }
            }

            SendPointer(_item, Selector("popUpStatusItemMenu:"), menu);
        }
        finally
        {
            Send(menu, Selector("release"));
            _items = [];
        }
    }

    private void OnTrayChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Update();

    private void Update()
    {
        var pool = Send(Class("NSAutoreleasePool"), Selector("new"));
        try
        {
            SendPointer(_button, Selector("setToolTip:"), String(_tray.ToolTip));
            var key = (_tray.Tone, _tray.Mark);
            if (_image == key)
            {
                return;
            }

            using var bitmap = TrayIconRenderer.Render(key.Tone, key.Mark);
            using var stream = new MemoryStream();
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            var bytes = stream.ToArray();
            var data = SendBytes(Class("NSData"), Selector("dataWithBytes:length:"), bytes, (nuint)bytes.Length);
            var image = SendPointer(Send(Class("NSImage"), Selector("alloc")), Selector("initWithData:"), data);
            SendSize(image, Selector("setSize:"), new ImageSize(18, 18));
            SendPointer(_button, Selector("setImage:"), image);
            Send(image, Selector("release"));
            _image = key;
        }
        finally
        {
            Send(pool, Selector("drain"));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _tray.PropertyChanged -= OnTrayChanged;
        _current = null;
        SendPointer(_bar, Selector("removeStatusItem:"), _item);
        Send(_target, Selector("release"));
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ImageSize(double Width, double Height);

    private delegate void NativeAction(nint self, nint command, nint sender);

    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern nint GetClass(byte[] name);

    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern nint GetSelector(byte[] name);

    [DllImport(ObjC, EntryPoint = "objc_allocateClassPair")]
    private static extern nint AllocateClass(nint parent, byte[] name, nuint size);

    [DllImport(ObjC, EntryPoint = "class_addMethod")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AddMethod(nint type, nint selector, NativeAction action, byte[] encoding);

    [DllImport(ObjC, EntryPoint = "objc_registerClassPair")]
    private static extern void RegisterClass(nint type);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint target, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendPointer(nint target, nint selector, nint argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendDouble(nint target, nint selector, double argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendBytes(nint target, nint selector, byte[] bytes, nuint count);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendSize(nint target, nint selector, ImageSize size);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendText(nint target, nint selector, byte[] text);

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + '\0');

    private static nint Class(string name) => GetClass(Utf8(name));

    private static nint Selector(string name) => GetSelector(Utf8(name));

    private static nint String(string value) => SendText(Class("NSString"), Selector("stringWithUTF8String:"), Utf8(value));
}
