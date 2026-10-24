using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>Короткая подсказка сверху окна («Серверы обновлены · пинг проверен»), сама исчезает.</summary>
public sealed partial class ToastViewModel : ObservableObject
{
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(1.8);

    private readonly DispatcherTimer _timer;

    public ToastViewModel()
    {
        _timer = new DispatcherTimer { Interval = Duration };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            IsVisible = false;
        };
    }

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsVisible { get; set; }

    /// <summary>Длинное сообщение (о выбранном сервере) держится дольше — его надо успеть прочесть.</summary>
    public static readonly TimeSpan LongDuration = TimeSpan.FromSeconds(6);

    public void Show(string text, TimeSpan? duration = null)
    {
        Text = text;
        IsVisible = true;
        _timer.Stop();
        _timer.Interval = duration ?? Duration;
        _timer.Start();
    }
}
