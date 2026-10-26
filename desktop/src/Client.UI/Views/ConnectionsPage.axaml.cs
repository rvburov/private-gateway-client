using Avalonia.Controls;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.UI.Views;

public partial class ConnectionsPage : UserControl
{
    public ConnectionsPage()
    {
        InitializeComponent();

        // Счетчики идут, пока страница на экране: ушла — служба больше их не шлет.
        AttachedToVisualTree += (_, _) => (DataContext as ConnectionsPageViewModel)?.Start();
        DetachedFromVisualTree += (_, _) => (DataContext as ConnectionsPageViewModel)?.Stop();
    }
}
