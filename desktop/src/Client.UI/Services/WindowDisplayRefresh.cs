using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.UI.Services;

/// <summary>
/// После смены экранов согласовать разметку и нативную поверхность окна. При отключении монитора macOS
/// сначала может сообщить временный экран, затем Retina: повтор после затихания событий берет уже текущие параметры.
/// Размеры остаются логическими; дополнительный ScaleTransform и пересоздание окна не нужны.
/// </summary>
public sealed class WindowDisplayRefresh : IDisposable
{
    public static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(250);

    private readonly Window _window;
    private readonly Dispatcher _dispatcher;
    private readonly Action<Window> _refreshNative;
    private readonly TimeProvider _time;
    private ITimer? _settled;
    private int _revision;
    private bool _queued;
    private bool _closePopups;
    private bool _refreshing;
    private bool _disposed;

    public WindowDisplayRefresh(Window window, Action<Window>? refreshNative = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        _dispatcher = Dispatcher.UIThread;
        _refreshNative = refreshNative ?? MacDisplayRefresh.Refresh;
        _time = time ?? TimeProvider.System;
        window.Opened += OnDisplayChanged;
        window.Activated += OnDisplayChanged;
        window.ScalingChanged += OnScreenChanged;
        window.Screens.Changed += OnScreenChanged;
        window.Closed += OnClosed;
        _dispatcher.ShutdownStarted += OnClosed;
    }

    /// <summary>Объединить серию уведомлений экрана; повторный вызов из нативного ScalingChanged не создает цикл.</summary>
    public void RequestRefresh(bool closePopups = false)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _refreshing)
        {
            return;
        }

        _closePopups |= closePopups;
        QueueRefresh();
        var revision = ++_revision;
        _settled?.Dispose();
        _settled = _time.CreateTimer(_ => _dispatcher.Post(() =>
        {
            if (!_disposed && revision == _revision)
            {
                QueueRefresh();
            }
        }), null, SettleDelay, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _dispatcher.VerifyAccess();
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settled?.Dispose();
        _window.Opened -= OnDisplayChanged;
        _window.Activated -= OnDisplayChanged;
        _window.ScalingChanged -= OnScreenChanged;
        _window.Screens.Changed -= OnScreenChanged;
        _window.Closed -= OnClosed;
        _dispatcher.ShutdownStarted -= OnClosed;
        GC.SuppressFinalize(this);
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => RequestRefresh();

    private void OnScreenChanged(object? sender, EventArgs e) => RequestRefresh(closePopups: true);

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    private void QueueRefresh()
    {
        if (_queued)
        {
            return;
        }

        _queued = true;
        // Вне обработчика AppKit и прохода разметки: displayIfNeeded может синхронно запустить отрисовку Avalonia.
        _dispatcher.Post(Refresh, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        _queued = false;
        if (_disposed || !_window.IsVisible || _window.WindowState == WindowState.Minimized)
        {
            return;
        }

        _refreshing = true;
        try
        {
            if (_closePopups)
            {
                _closePopups = false;
                MainWindow.CloseDropDowns(_window);
            }
            foreach (var visual in _window.GetVisualDescendants().Prepend(_window))
            {
                if (visual is Control control)
                {
                    control.InvalidateMeasure();
                    control.InvalidateArrange();
                }

                visual.InvalidateVisual();
            }

            _window.UpdateLayout();
            _refreshNative(_window);
        }
        finally
        {
            _refreshing = false;
        }
    }
}
