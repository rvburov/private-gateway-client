using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// Переключатели с вопросом (блок при обрыве, строгий режим, прокси для локальной сети): нажатие мышью — вопрос; отказ
/// возвращает переключатель на место на экране, а не только в настройках; согласие — переключает.
/// </summary>
public sealed class ConfirmToggleTests : WindowHarness
{
    [AvaloniaTheory]
    [InlineData(Pages.Settings, "KillSwitchToggle", true, "DialogPrimary")]
    [InlineData(Pages.Protection, "KillSwitchToggle", true, "DialogPrimary")]
    [InlineData(Pages.Protection, "StrictToggle", false, "DialogSecondary")]
    [InlineData(Pages.Core, "LanProxyToggle", false, "DialogPrimary")]
    public void Declining_puts_the_switch_back(string page, string name, bool before, string decline)
    {
        var (window, toggle) = OpenAt(page, name);
        toggle.IsChecked.ShouldBe(before);

        Click(window, toggle);
        Find<Button>(window, "DialogPrimary").IsEffectivelyVisible.ShouldBeTrue("сначала — вопрос");
        Press(window, decline);

        toggle.IsChecked.ShouldBe(before, "отказ — переключатель на прежнем месте");
    }

    [AvaloniaTheory]
    [InlineData(Pages.Settings, "KillSwitchToggle", true, "DialogSecondary")]
    [InlineData(Pages.Protection, "StrictToggle", false, "DialogPrimary")]
    [InlineData(Pages.Core, "LanProxyToggle", false, "DialogSecondary")]
    public void Agreeing_switches_it(string page, string name, bool before, string agree)
    {
        var (window, toggle) = OpenAt(page, name);

        Click(window, toggle);
        Press(window, agree);

        toggle.IsChecked.ShouldBe(!before);
    }

    private (Window Window, ToggleButton Toggle) OpenAt(string page, string name)
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(page);
        Dispatcher.UIThread.RunJobs();
        var toggle = Find<ToggleButton>(window, name);
        toggle.IsEffectivelyEnabled.ShouldBeTrue();
        return (window, toggle);
    }

    /// <summary>Переключатель — в верх страницы и нажатие мышью, как у пользователя.</summary>
    private static void Click(Window window, Control control)
    {
        var scroll = control.FindAncestorOfType<ScrollViewer>()!;
        var top = control.TranslatePoint(default, (Visual)scroll.Content!)!.Value.Y;
        scroll.Offset = new Vector(0, Math.Max(0, top - 120));
        Dispatcher.UIThread.RunJobs();
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(center, MouseButton.Left);
        window.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }
}
