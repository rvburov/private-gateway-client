using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Application.Updates;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Infrastructure.Demo;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>Меню значка в трее (доска 8): системное меню ОС, те же пункты, что на доске.</summary>
public sealed class TrayTests : WindowHarness
{
    [AvaloniaFact]
    public void Menu_shows_the_state_and_turns_protection_on_and_off()
    {
        var (_, shell) = Open();
        var tray = Service<TrayViewModel>();

        Headers(tray.Menu).ShouldBe(
        [
            "IP открыт · NL · Нидерланды 01 · 42 мс", "—", "Включить защиту", "Сменить сервер", "Обновить серверы", "—",
            "Открыть окно", "Проверка", "—", "Закрыть программу",
        ]);
        ((NativeMenuItem)tray.Menu.Items[0]).IsEnabled.ShouldBeFalse("строка состояния — не кнопка");
        tray.ToolTip.ShouldBe("Private Gateway · IP открыт");

        var toggle = Item(tray.Menu, "Включить защиту").Command!;
        toggle.Execute(null);
        Time.Advance(SimulatedGatewayClient.ConnectDelay);
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Connected);
        Dispatcher.UIThread.RunJobs();

        Headers(tray.Menu)[0].ShouldBe("IP скрыт · NL · Нидерланды 01 · 42 мс");
        (tray.Tone, tray.Mark).ShouldBe((Tone.Connected, "Icon.Mark.Ok"));
        Item(tray.Menu, "Выключить защиту").Command!.Execute(null);
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Off);
    }

    [AvaloniaFact]
    public void Quick_switch_lists_the_selected_favorites_and_recent()
    {
        var (_, _) = Open();
        var tray = Service<TrayViewModel>();
        var servers = Item(tray.Menu, "Сменить сервер").Menu!;

        Headers(servers).ShouldBe(
        [
            "NL · Нидерланды 01 · 42 мс", "FI · Финляндия 01 · 38 мс", "DE · Германия 02 · 51 мс", "SE · Швеция 01 · 57 мс", "—", "Все серверы…",
        ]);
        servers.Items.OfType<NativeMenuItem>().Where(i => i.IsChecked).Select(i => i.Header).ShouldBe(["NL · Нидерланды 01 · 42 мс"]);

        Item(servers, "FI · Финляндия 01 · 38 мс").Command!.Execute("demo-fi-01");
        WaitFor(() => Service<IServerCatalog>().Selected?.Id == "demo-fi-01");
        Dispatcher.UIThread.RunJobs();

        Headers(tray.Menu)[0].ShouldBe("IP открыт · FI · Финляндия 01 · 38 мс");

        var catalog = Service<IServerCatalog>();
        catalog.Choose("demo-nl-01");
        foreach (var id in new[] { "demo-nl-01", "demo-fi-01", "demo-de-02" })
        {
            catalog.SetFavorite(id, false);
        }

        Dispatcher.UIThread.RunJobs();
        tray.Quick().Select(s => s.Id).ShouldBe(["demo-nl-01", "demo-fi-01", "demo-de-02", "demo-se-01"], "недавние — с последнего");
    }

    [AvaloniaFact]
    public void Window_items_show_the_window_on_the_right_page_and_quit_asks_to_exit()
    {
        var (_, shell) = Open();
        var tray = Service<TrayViewModel>();
        var shown = 0;
        var quit = 0;
        tray.ShowWindowRequested += (_, _) => shown++;
        tray.QuitRequested += (_, _) => quit++;

        Execute(Item(tray.Menu, "Проверка"));
        shell.CurrentKey.ShouldBe(Pages.Check);
        Execute(Item(Item(tray.Menu, "Сменить сервер").Menu!, "Все серверы…"));
        shell.CurrentKey.ShouldBe(Pages.Servers);
        Execute(Item(tray.Menu, "Открыть окно"));
        Execute(Item(tray.Menu, "Закрыть программу"));

        (shown, quit).ShouldBe((3, 1));
    }

    [AvaloniaFact]
    public void Lifting_a_block_from_the_tray_opens_the_window_for_the_question()
    {
        var (_, shell) = Open();
        var tray = Service<TrayViewModel>();
        var shown = 0;
        tray.ShowWindowRequested += (_, _) => shown++;
        _ = Service<ConnectionController>().ConnectToAsync("demo-tr-01");
        Time.Advance(SimulatedGatewayClient.ConnectDelay);
        WaitFor(() => shell.Protection.Status.State == ConnectionState.Blocked);
        Dispatcher.UIThread.RunJobs();

        Item(tray.Menu, "Выключить защиту").Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();

        shown.ShouldBe(1, "снять блокировку — с вопросом, а он в окне");
        shell.Overlay.Dialog.ShouldNotBeNull();
    }

    [AvaloniaFact]
    public void Menu_follows_the_language()
    {
        var (_, _) = Open();
        var tray = Service<TrayViewModel>();

        Loc.Instance.SetLanguage("en");
        Dispatcher.UIThread.RunJobs();

        Headers(tray.Menu).ShouldContain("Switch server");
        Headers(tray.Menu).ShouldContain("Quit");
    }

    [AvaloniaFact]
    public void Icon_is_drawn_in_the_state_color()
    {
        var (_, _) = Open();

        using var on = TrayIconRenderer.Render(Tone.Connected, "Icon.Mark.Ok");
        using var off = TrayIconRenderer.Render(Tone.Off, "Icon.Mark.Off");

        on.PixelSize.ShouldBe(new Avalonia.PixelSize(64, 64));
        Png(on).ShouldNotBe(Png(off));
    }

    [AvaloniaFact]
    public void Autostart_shows_only_the_tray_icon_after_the_first_launch()
    {
        var (window, _) = Open();
        window.Hide();
        var settings = Service<SettingsService>();

        var autostart = Host(window, minimized: true);
        autostart.MainWindow.ShouldBeNull("запуск с --minimized после первого запуска — окно не показывается, только значок");
        window.IsVisible.ShouldBeFalse();

        Service<NoticeWindow>().Bring();
        (autostart.MainWindow, window.IsVisible).ShouldBe((window, true), "нажали уведомление — окно показано");

        Host(window, minimized: false).MainWindow.ShouldBe(window, "обычный запуск — окно");
        settings.Update(s => s with { OnboardingDone = false });
        Host(window, minimized: true).MainWindow.ShouldBe(window, "первый запуск — приветствие и при --minimized");
    }

    // Жизненный цикл окна без Start: DesktopHost только назначает ему главное окно.
    private ClassicDesktopStyleApplicationLifetime Host(MainWindow window, bool minimized)
    {
        var lifetime = new ClassicDesktopStyleApplicationLifetime();
        _ = new DesktopHost(Avalonia.Application.Current!, lifetime, window, Service<TrayViewModel>(), Service<SettingsService>(), null, minimized,
            Service<NoticeWindow>(), Service<UpdateWatcher>());
        return lifetime;
    }

    private static List<string> Headers(NativeMenu menu) =>
        [.. menu.Items.Select(i => i is NativeMenuItem item and not NativeMenuItemSeparator ? item.Header ?? string.Empty : "—")];

    private static NativeMenuItem Item(NativeMenu menu, string header) =>
        menu.Items.OfType<NativeMenuItem>().Single(i => i is not NativeMenuItemSeparator && i.Header == header);

    private static void Execute(NativeMenuItem item) => item.Command!.Execute(item.CommandParameter);

    private static string Png(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return Convert.ToBase64String(stream.ToArray());
    }
}
