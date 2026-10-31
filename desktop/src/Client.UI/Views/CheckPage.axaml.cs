using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.UI.Views;

public partial class CheckPage : UserControl
{
    public CheckPage() => InitializeComponent();

    /// <summary>Открыли плиткой «Пинг» — сразу к блоку «Задержка», когда экран разложен.</summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is CheckPageViewModel page && page.TakeFocusDelay())
        {
            Dispatcher.UIThread.Post(ScrollToDelay, DispatcherPriority.Background);
        }
    }

    private void ScrollToDelay()
    {
        if (DelayHeader.FindAncestorOfType<ScrollViewer>() is { Content: Visual content } viewer && DelayHeader.TranslatePoint(default, content) is { } at)
        {
            viewer.Offset = new Vector(viewer.Offset.X, Math.Max(0, at.Y - 8));
        }
    }
}
