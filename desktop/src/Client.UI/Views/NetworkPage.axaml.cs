using Avalonia.Controls;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.UI.Views;

public partial class NetworkPage : UserControl
{
    public NetworkPage()
    {
        InitializeComponent();

        // Страница открыта снова — снимок заново: сеть могла смениться, пока ее не было на экране.
        AttachedToVisualTree += (_, _) => (DataContext as NetworkPageViewModel)?.RefreshCommand.Execute(null);
    }
}
