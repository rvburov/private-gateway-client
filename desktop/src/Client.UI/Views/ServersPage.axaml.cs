using Avalonia.Controls;
using Avalonia.Input;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.UI.Views;

public partial class ServersPage : UserControl
{
    public ServersPage()
    {
        InitializeComponent();
        // Ушли со страницы (островок, «+») — панель фильтра не ждет возврата открытой.
        DetachedFromVisualTree += (_, _) => (DataContext as ServersPageViewModel)?.CloseFilterCommand.Execute(null);
    }

    private void OnDismissFilter(object? sender, PointerPressedEventArgs e)
    {
        (DataContext as ServersPageViewModel)?.CloseFilterCommand.Execute(null);
        e.Handled = true;
    }
}
