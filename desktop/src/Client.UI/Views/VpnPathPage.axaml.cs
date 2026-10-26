using Avalonia.Controls;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.UI.Views;

public partial class VpnPathPage : UserControl
{
    public VpnPathPage()
    {
        InitializeComponent();

        // Страница открыта — сеть и снимок «до» заново: защита могла включиться или выключиться.
        AttachedToVisualTree += (_, _) => (DataContext as VpnPathPageViewModel)?.RefreshCommand.Execute(null);
    }
}
