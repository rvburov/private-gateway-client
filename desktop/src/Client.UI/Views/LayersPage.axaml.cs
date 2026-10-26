using Avalonia.Controls;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.UI.Views;

public partial class LayersPage : UserControl
{
    public LayersPage()
    {
        InitializeComponent();

        // Страница открыта — сеть и сокеты заново: факты под объяснениями — сегодняшние.
        AttachedToVisualTree += (_, _) => (DataContext as LayersPageViewModel)?.RefreshCommand.Execute(null);
    }
}
