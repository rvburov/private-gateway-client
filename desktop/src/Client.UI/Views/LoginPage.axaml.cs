using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.UI.Views;

public partial class LoginPage : UserControl
{
    private LoginPageViewModel? _model;
    private bool _attached;

    public LoginPage() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Watch(DataContext as LoginPageViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        Watch(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Watch(_attached ? DataContext as LoginPageViewModel : null);
    }

    // Модель живет дольше страницы: подписка — только пока страница в окне.
    private void Watch(LoginPageViewModel? model)
    {
        if (_model is not null)
        {
            _model.PropertyChanged -= OnModelChanged;
        }

        _model = model;
        if (_model is not null)
        {
            _model.PropertyChanged += OnModelChanged;
        }
    }

    // Смена шага переносит фокус: второй шаг — в поле кода (код вводят сразу), первый — в почту, выделенную целиком:
    // «Войти другим аккаунтом» вводит другую почту поверх прежней.
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LoginPageViewModel.IsCodeStep) || sender is not LoginPageViewModel model)
        {
            return;
        }

        var codeStep = model.IsCodeStep;
        Dispatcher.UIThread.Post(() =>
        {
            if (codeStep)
            {
                LoginCode.Focus();
            }
            else
            {
                LoginEmail.Focus();
                LoginEmail.SelectAll();
            }
        }, DispatcherPriority.Loaded);
    }
}
