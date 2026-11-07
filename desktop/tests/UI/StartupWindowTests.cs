using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.Tests.UI;

public sealed class StartupWindowTests : WindowHarness
{
    [AvaloniaFact]
    public async Task First_frame_is_visible_while_initialization_waits_and_the_window_stays_responsive()
    {
        var window = new MainWindow();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = false;
        var start = window.StartAsync(async () =>
        {
            window.IsVisible.ShouldBeTrue("медленная подготовка начинается только после показа окна");
            entered = true;
            await release.Task;
        }, show: true);
        try
        {
            window.IsVisible.ShouldBeTrue();
            window.FindControl<StackPanel>("StartupPanel")!.IsVisible.ShouldBeTrue();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            entered.ShouldBeTrue();
            start.IsCompleted.ShouldBeFalse();
            var responsive = false;
            await Dispatcher.UIThread.InvokeAsync(() => responsive = true);
            responsive.ShouldBeTrue();
            Snapshot(window, "startup-loading");
            release.SetResult();
            await start;
        }
        finally
        {
            release.TrySetResult();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Background_start_does_not_flash_a_window()
    {
        var window = new MainWindow();
        try
        {
            await window.StartAsync(() => Task.CompletedTask, show: false);
            window.IsVisible.ShouldBeFalse();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Startup_failure_has_a_close_button_instead_of_an_endless_spinner()
    {
        var window = new MainWindow();
        try
        {
            window.ShowStartupFailure();
            window.IsVisible.ShouldBeTrue();
            window.FindControl<ProgressBar>("StartupProgress")!.IsVisible.ShouldBeFalse();
            window.FindControl<TextBlock>("StartupError")!.IsVisible.ShouldBeTrue();
            window.FindControl<Button>("StartupClose")!.IsVisible.ShouldBeTrue();
            Snapshot(window, "startup-failed");
        }
        finally
        {
            window.Close();
        }
    }
}
