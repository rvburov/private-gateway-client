using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace PrivateGateway.Client.UI.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        AddHandler(ScrollViewer.ScrollChangedEvent, OnScrolled, RoutingStrategies.Bubble);
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
