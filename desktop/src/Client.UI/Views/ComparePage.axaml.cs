using Avalonia.Controls;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.UI.Views;

public partial class ComparePage : UserControl
{
    public ComparePage()
    {
        InitializeComponent();

        // Страница открыта — «сейчас» заново: защита и сеть могли смениться.
        AttachedToVisualTree += (_, _) => (DataContext as ComparePageViewModel)?.RunCommand.Execute(null);
    }
}
