using Avalonia.Controls;
using Avalonia.Threading;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.UI.Views;

public partial class DialogView : UserControl
{
    public DialogView() => InitializeComponent();

    // Новый диалог забирает фокус на безопасную кнопку: Enter не выберет опасное действие.
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is DialogViewModel dialog)
        {
            var safe = dialog.IsPrimarySafe ? DialogPrimary : DialogSecondary;
            Dispatcher.UIThread.Post(() => safe.Focus(), DispatcherPriority.Loaded);
        }
    }
}
