using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;

namespace PrivateGateway.Client.UI.Services;

/// <summary>Только --check-display: настоящий Metal drawable в отдельном демо-окне, без службы и пользовательских данных.</summary>
internal static class MacDisplayCheck
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    public static void Run(Window window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                Check(window);
                Console.WriteLine("Масштаб macOS: Metal drawable восстановлен после уменьшения и увеличения поверхности");
                desktop.Shutdown();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("Масштаб macOS: " + error.Message);
                desktop.Shutdown(1);
            }
        };
        desktop.Exit += (_, _) => timer.Stop();
        timer.Start();
    }

    private static void Check(Window window)
    {
        if (window.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle handle || handle.NSView == 0)
        {
            throw new InvalidOperationException("нет настоящего NSView");
        }

        var view = handle.NSView;
        var layer = Send(view, Selector("layer"));
        var getSize = Selector("drawableSize");
        var setSize = Selector("setDrawableSize:");
        if (layer == 0 || SendSelector(layer, Selector("respondsToSelector:"), getSize) == 0)
        {
            throw new InvalidOperationException("проверка требует Metal drawable, другой движок не считается успехом");
        }

        var expected = SendSizeArgument(view, Selector("convertSizeToBacking:"), new NativeSize(window.ClientSize.Width, window.ClientSize.Height));
        if (expected.Width <= 1 || expected.Height <= 1)
        {
            throw new InvalidOperationException("окно еще не получило размер поверхности");
        }

        foreach (var factor in new[] { .5, 2d })
        {
            var wrong = new NativeSize(Math.Round(expected.Width * factor), Math.Round(expected.Height * factor));
            SendSetSize(layer, setSize, wrong);
            if (SendSize(layer, getSize) != wrong || wrong == expected)
            {
                throw new InvalidOperationException("стенд не воспроизвел устаревший размер поверхности");
            }

            MacDisplayRefresh.Refresh(window);
            var actual = SendSize(layer, getSize);
            if (actual != expected)
            {
                throw new InvalidOperationException($"drawable не восстановлен: {actual.Width}x{actual.Height}, ожидалось {expected.Width}x{expected.Height}");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeSize(double Width, double Height);

    private static nint Selector(string name) => GetSelector(Encoding.UTF8.GetBytes(name + '\0'));

    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern nint GetSelector(byte[] name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern byte SendSelector(nint receiver, nint selector, nint argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern NativeSize SendSize(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern NativeSize SendSizeArgument(nint receiver, nint selector, NativeSize size);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendSetSize(nint receiver, nint selector, NativeSize size);
}
