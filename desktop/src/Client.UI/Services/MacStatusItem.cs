using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.UI.Services;

/// <summary>
/// Значок строки меню macOS. Avalonia привязывает к нему меню AppKit и не получает события кнопок мыши.
/// Левое нажатие открывает окно. Правое нажатие показывает меню AppKit.
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
        // NSStatusBar не удерживает выданный объект: после первого оборота AppKit освобождал значок и его кнопку.
        _item = Send(SendDouble(_bar, Selector("statusItemWithLength:"), -2), Selector("retain"));
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
            current.OpenMenu();
        }
        else
        {
            Dispatcher.UIThread.Post(current._show);
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

    private void OpenMenu() => WithMenu(menu => SendPointer(_item, Selector("popUpStatusItemMenu:"), menu));

    private void WithMenu(Action<nint> useMenu)
    {
        _items = [.. _tray.Menu.Items];
        var menu = Send(Class("NSMenu"), Selector("new"));
        SendPointer(menu, Selector("setAutoenablesItems:"), 0);
        try
        {
            for (var i = 0; i < _items.Length; i++)
            {
                // NativeMenuItemSeparator наследует NativeMenuItem: его заголовок «-» не является пунктом меню.
                if (_items[i] is NativeMenuItem item && item is not NativeMenuItemSeparator)
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

            useMenu(menu);
        }
        finally
        {
            Send(menu, Selector("release"));
            _items = [];
        }
    }

    private void OnTrayChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Update();
        }
        else
        {
            Dispatcher.UIThread.Post(Update);
        }
    }

    private void Update()
    {
        if (_disposed)
        {
            return;
        }

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
        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }
        SendPointer(_bar, Selector("removeStatusItem:"), _item);
        Send(_item, Selector("release"));
        Send(_target, Selector("release"));
    }

    // Проверяем уже созданные элементы AppKit, а не только модель меню Avalonia.
    private bool CheckMenuSeparators()
    {
        var valid = true;
        WithMenu(menu =>
        {
            valid = Send(menu, Selector("numberOfItems")) == _items.Length;
            var separators = 0;
            for (var i = 0; valid && i < _items.Length; i++)
            {
                var expected = _items[i] is NativeMenuItemSeparator;
                var row = SendPointer(menu, Selector("itemAtIndex:"), i);
                var actual = (Send(row, Selector("isSeparatorItem")) & 0xff) != 0;
                valid &= actual == expected;
                separators += actual ? 1 : 0;
            }

            valid &= separators == 4;
        });
        return valid;
    }

    // Только отдельный процесс CI: тот же запуск после первого кадра и настоящий цикл AppKit, служба — имитация.
    internal static void CheckLifetime(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var item = _current ?? throw new InvalidOperationException("Нативный значок не создан");
        var turns = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            // Между тактами AppKit очищает свои autorelease pools — как при обычном запуске приложения.
            Localization.Loc.Instance.SetLanguage(turns % 2 == 0 ? "en" : "ru");
            item.Update();
            if (!item.CheckMenuSeparators())
            {
                timer.Stop();
                Console.Error.WriteLine("Разделители меню macOS: вместо системных линий созданы обычные пункты");
                desktop.Shutdown(1);
                return;
            }

            if (Send(item._item, Selector("button")) != item._button || Send(item._button, Selector("image")) == 0)
            {
                throw new InvalidOperationException("Значок не пережил очистку autorelease pool");
            }

            if (++turns < 8)
            {
                return;
            }

            timer.Stop();
            // Обновление уже в очереди при завершении не должно обращаться к освобожденной кнопке.
            Dispatcher.UIThread.Post(item.Update);
            item.Dispose();
            Dispatcher.UIThread.RunJobs();
            item.Dispose();
            Console.WriteLine("Трей macOS: 4 системных разделителя; значок пережил очистку памяти, обновление и завершение");
            desktop.Shutdown();
        };
        desktop.Exit += (_, _) => timer.Stop();
        timer.Start();
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
