using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.Views;

public partial class MainWindow : Window
{
    private Action? _closeStartup;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            ShellPanel.IsVisible = DataContext is not null;
            StartupPanel.IsVisible = DataContext is null;
        };
        AddHandler(ScrollViewer.ScrollChangedEvent, OnScrolled, RoutingStrategies.Bubble);
    }

    /// <summary>Сначала кадр окна, потом подготовка данных; при запуске в трей окно остается скрытым.</summary>
    public async Task StartAsync(Func<Task> initialize, bool show, CancellationToken cancellationToken = default)
    {
        if (show)
        {
            Show();
            var frame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            RequestAnimationFrame(_ => frame.TrySetResult());
            await frame.Task.WaitAsync(cancellationToken);
            StartupTiming.Mark("window-frame");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await initialize();
    }

    public void ShowStartupFailure(Action? close = null)
    {
        _closeStartup = close;
        ShellPanel.IsVisible = false;
        StartupPanel.IsVisible = true;
        StartupProgress.IsVisible = false;
        StartupError.IsVisible = true;
        StartupClose.IsVisible = true;
        Show();
    }

    private void CloseStartup(object? sender, RoutedEventArgs e)
    {
        if (_closeStartup is { } close)
        {
            close();
        }
        else
        {
            Close();
        }
    }

    /// <summary>
    /// Прокрутка страницы закрывает открытые выпадающие списки, как системные списки Windows и macOS (просьба пользователя
    /// 2026-11-05): список — отдельный слой у кнопки выбора, и при прокрутке он ехал вместе со страницей поверх нижнего
    /// меню. Закрывается только при сдвиге (<see cref="ScrollChangedEventArgs.OffsetDelta"/>): рост страницы от новых данных
    /// список не трогает. Прокрутка внутри самого списка сюда не доходит — у списка свое окно.
    /// </summary>
    private void OnScrolled(object? sender, ScrollChangedEventArgs e)
    {
        if (e.OffsetDelta == default)
        {
            return;
        }

        CloseDropDowns(this);
    }

    /// <summary>Закрыть открытые выпадающие списки окна <paramref name="root"/>.</summary>
    public static void CloseDropDowns(Visual root)
    {
        ArgumentNullException.ThrowIfNull(root);
        foreach (var popup in root.GetVisualDescendants().OfType<Popup>().Where(p => p.IsOpen).ToList())
        {
            popup.IsOpen = false;
        }
    }
}
