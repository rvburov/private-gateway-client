using Avalonia.Controls;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.UI.Views;

public partial class PathPage : UserControl
{
    public PathPage()
    {
        InitializeComponent();

        // Страница открыта — сеть машины заново: адаптер и шлюз могли смениться.
        AttachedToVisualTree += (_, _) => (DataContext as PathPageViewModel)?.RefreshCommand.Execute(null);
    }
}
