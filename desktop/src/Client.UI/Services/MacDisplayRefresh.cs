using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;

namespace PrivateGateway.Client.UI.Services;

/// <summary>
/// Avalonia 12.1.4 / macOS: после hotplug управляемый DPI может быть правильным, а Metal drawable — прежним
/// (upstream #22684). Повторить штатное обновление backing properties именно AvnView и отрисовать его слой
/// на главном потоке: так native Metal применяет PendingSize/PendingScaling до следующего фонового кадра.
/// Публичные методы AppKit, без подмены DPI, приватных полей Avalonia и переключения графического движка.
/// </summary>
internal static class MacDisplayRefresh
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    public static void Refresh(Window window)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!OperatingSystem.IsMacOS() || window.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle handle)
        {
            return;
        }

        var view = handle.NSView;
        if (view == 0)
        {
            return;
        }

        var backing = Selector("viewDidChangeBackingProperties");
        var layerSelector = Selector("layer");
        if (!Responds(view, backing) || !Responds(view, layerSelector))
        {
            return;
        }

        // NSView получен на время этого вызова. После закрытия окна указатель не сохраняется и не используется.
        SendVoid(view, backing);
        var layer = Send(view, layerSelector);
        var display = Selector("displayIfNeeded");
        if (layer != 0 && Responds(layer, display))
        {
            SendVoid(layer, display);
        }
    }

    private static bool Responds(nint receiver, nint selector) =>
        SendSelector(receiver, Selector("respondsToSelector:"), selector) != 0;

    private static nint Selector(string name) => GetSelector(Encoding.UTF8.GetBytes(name + '\0'));

    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern nint GetSelector(byte[] name);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern byte SendSelector(nint receiver, nint selector, nint argument);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(nint receiver, nint selector);
}
