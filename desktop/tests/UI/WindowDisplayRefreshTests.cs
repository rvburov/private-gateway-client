using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>Смена DPI без нативного монитора; настоящий drawable проверяется отдельно на macOS.</summary>
public sealed class WindowDisplayRefreshTests : WindowHarness
{
    [AvaloniaFact]
    public void A_burst_uses_the_current_scale_and_only_one_settled_refresh()
    {
        var window = new Window();
        window.Show();
        // SetRenderScaling в Headless сначала сам прокачивает очередь. Устанавливаем экран до серии уведомлений.
        window.SetRenderScaling(2);
        var scales = new List<double>();
        using var refresh = new WindowDisplayRefresh(window, w => scales.Add(w.RenderScaling), Time);
        try
        {
            for (var i = 0; i < 30; i++)
            {
                refresh.RequestRefresh();
            }

            Dispatcher.UIThread.RunJobs();
            scales.ShouldBe([2d], "события не превращаются в очередь из 30 нативных отрисовок");
            Time.Advance(WindowDisplayRefresh.SettleDelay);
            Dispatcher.UIThread.RunJobs();
            scales.ShouldBe([2d, 2d]);
            Time.Advance(TimeSpan.FromMinutes(10));
            Dispatcher.UIThread.RunJobs();
            scales.Count.ShouldBe(2, "постоянного опроса экрана нет");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Native_backing_notification_does_not_schedule_itself_again()
    {
        var window = new Window();
        window.Show();
        var calls = 0;
        using var refresh = new WindowDisplayRefresh(window, w =>
        {
            calls++;
            // Как viewDidChangeBackingProperties: native возвращает актуальный масштаб в Avalonia.
            if (calls == 1)
            {
                w.SetRenderScaling(2);
            }
        }, Time);
        try
        {
            refresh.RequestRefresh();
            Dispatcher.UIThread.RunJobs();
            calls.ShouldBe(1);
            Time.Advance(WindowDisplayRefresh.SettleDelay);
            Dispatcher.UIThread.RunJobs();
            calls.ShouldBe(2, "только предусмотренный завершающий проход, без рекурсивного repaint");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void A_closed_window_cancels_queued_and_settled_native_calls()
    {
        var window = new Window();
        window.Show();
        var calls = 0;
        using var refresh = new WindowDisplayRefresh(window, _ => calls++, Time);
        refresh.RequestRefresh();
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Time.Advance(WindowDisplayRefresh.SettleDelay);
        Dispatcher.UIThread.RunJobs();
        calls.ShouldBe(0, "указатель закрытого NSView нельзя использовать отложенным событием");
    }

    [AvaloniaFact]
    public void Disposing_the_window_owner_cancels_callbacks_before_graphics_resources_are_released()
    {
        var (window, _) = Open();
        var calls = 0;
        using var refresh = new WindowDisplayRefresh(window, _ => calls++, Time);
        refresh.RequestRefresh();

        Dispose();

        window.IsVisible.ShouldBeFalse("владелец закрывает окно до освобождения графических ресурсов Headless");
        Dispatcher.UIThread.RunJobs();
        Time.Advance(WindowDisplayRefresh.SettleDelay);
        Dispatcher.UIThread.RunJobs();
        calls.ShouldBe(0, "запланированная перерисовка не переживает окно и его контейнер сервисов");
    }

    [AvaloniaFact]
    public void A_hidden_window_waits_for_show_instead_of_drawing_a_detached_surface()
    {
        var window = new Window();
        var calls = 0;
        using var refresh = new WindowDisplayRefresh(window, _ => calls++, Time);
        try
        {
            refresh.RequestRefresh();
            Dispatcher.UIThread.RunJobs();
            calls.ShouldBe(0);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            calls.ShouldBe(1);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Monitor_scale_changes_keep_logical_layout_navigation_and_the_existing_connection()
    {
        var (window, shell) = Open();
        await Connect(shell);
        shell.NavigateCommand.Execute(Pages.Check);
        Dispatcher.UIThread.RunJobs();
        var original = window.ClientSize;
        var button = Find<Button>(window, "RunButton");
        var bounds = button.Bounds;
        var connectedAt = shell.Protection.Status.Since;
        foreach (var scaling in new[] { 2d, 1d, 1.5, 2d })
        {
            window.SetRenderScaling(scaling);
            Dispatcher.UIThread.RunJobs();
            window.ClientSize.ShouldBe(original, "логические размеры не умножаются на Retina/DPI второй раз");
            button.Bounds.ShouldBe(bounds);
            button.TranslatePoint(default, window)!.Value.X.ShouldBeGreaterThanOrEqualTo(0);
            var right = button.TranslatePoint(new Point(button.Bounds.Width, 0), window)!.Value.X;
            right.ShouldBeLessThanOrEqualTo(original.Width);
            shell.Protection.Status.State.ShouldBe(ConnectionState.Connected);
            shell.Protection.Status.Since.ShouldBe(connectedAt, "перестройка графики не переподключает VPN");
        }

        shell.NavigateCommand.Execute(Pages.Settings);
        Dispatcher.UIThread.RunJobs();
        shell.CurrentPage.ShouldBeOfType<SettingsPageViewModel>();
        Snapshot(window, "display-retina-restored");
    }

    [AvaloniaFact]
    public async Task Retina_snapshots_keep_the_pixel_buffer_alive_during_png_encoding_and_collection()
    {
        var (window, _) = Open();
        window.SetRenderScaling(2);
        using var collect = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var started = new ManualResetEventSlim();
        var collections = 0;
        var collector = Task.Run(() =>
        {
            started.Set();
            while (!collect.IsCancellationRequested)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                Interlocked.Increment(ref collections);
                collect.Token.WaitHandle.WaitOne(1);
            }
        });
        try
        {
            started.Wait();
            for (var i = 0; i < 3; i++)
            {
                var name = $"display-retina-gc-{i}";
                Snapshot(window, name);
                using var decoded = new Bitmap(Path.Combine(Repo.Snapshots, name + ".png"));
                decoded.PixelSize.ShouldBe(PixelSize.FromSize(window.ClientSize, window.RenderScaling));
            }
        }
        finally
        {
            collect.Cancel();
            await collector;
        }
        collections.ShouldBeGreaterThan(0, "GC работал одновременно с нативным PNG-кодировщиком");
    }

    [AvaloniaFact]
    public void A_screen_change_closes_old_popups_but_a_plain_redraw_keeps_them_open()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Core);
        Dispatcher.UIThread.RunJobs();
        var page = shell.CurrentPage.ShouldBeOfType<CorePageViewModel>();
        var popup = window.GetVisualDescendants().OfType<Popup>().Single(p => p.PlacementTarget?.Name == "FingerprintChoice");
        page.ToggleFingerprintCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        popup.IsOpen.ShouldBeTrue();

        using var refresh = new WindowDisplayRefresh(window, _ => { }, Time);
        refresh.RequestRefresh();
        Dispatcher.UIThread.RunJobs();
        popup.IsOpen.ShouldBeTrue("активация и обычная перерисовка не отнимают открытый список");

        window.SetRenderScaling(2);
        Dispatcher.UIThread.RunJobs();
        popup.IsOpen.ShouldBeFalse("список не остается на прежнем экране со старым масштабом");
        page.IsFingerprintOpen.ShouldBeFalse();
    }
}
