using Avalonia.Controls;
using PrivateGateway.Client.Application.Notifications;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.UI.Services;

/// <summary>
/// Окно глазами уведомлений: активно ли оно, видно ли, всплывашка в нем и показ по нажатию на уведомление. Окно и способ
/// его показать отдает <see cref="DesktopHost"/>; без него (тесты без экрана) окна будто нет — уведомления идут в ОС.
/// </summary>
public sealed class NoticeWindow(ToastViewModel toast, INoticeTexts texts) : INoticeWindow
{
    private Window? _window;
    private Action? _show;

    public void Attach(Window window, Action show)
    {
        _window = window;
        _show = show;
    }

    public bool IsActive => _window is { IsVisible: true, IsActive: true, WindowState: not WindowState.Minimized };

    public bool IsVisible => _window is { IsVisible: true, WindowState: not WindowState.Minimized };

    /// <summary>Пропал и изменился выбранный сервер — длинный текст, как в окне раньше; остальное — «заголовок · текст».</summary>
    public void ShowInWindow(Notice notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var text = notice.Kind switch
        {
            NoticeKind.ServerGone => Loc.Instance.Format("subs.notice.missing", notice.Server, notice.Subscription),
            NoticeKind.ServerChanged => Loc.Instance.Format("subs.notice.changed", notice.Server, notice.Subscription),
            _ => texts.Text(notice) is var t ? t.Title + " · " + t.Body : string.Empty,
        };
        toast.Show(text, ToastViewModel.LongDuration);
    }

    /// <summary>Показать окно: нажали уведомление ОС.</summary>
    public void Bring() => _show?.Invoke();
}
