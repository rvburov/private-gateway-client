using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrivateGateway.Client.UI.Controls;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.Tests.UI;

public sealed class CountryFlagTests : WindowHarness
{
    [AvaloniaFact]
    public void Flags_and_earth_share_the_approved_frame_and_load_without_system_emoji_fonts()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.Servers);
        Dispatcher.UIThread.RunJobs();
        var flags = window.GetVisualDescendants().OfType<CountryFlag>().ToArray();
        flags.Length.ShouldBeGreaterThan(0);
        foreach (var flag in flags)
        {
            flag.Bounds.Width.ShouldBe(28);
            flag.Bounds.Height.ShouldBe(28);
            var frame = flag.FindControl<Border>("Frame").ShouldNotBeNull();
            frame.CornerRadius.TopLeft.ShouldBe(5);
            frame.BorderThickness.Left.ShouldBe(1);
            frame.Bounds.Height.ShouldBe(28);
            frame.Background.ShouldBeOfType<ImageBrush>().Stretch.ShouldBe(Stretch.UniformToFill);
        }

        CountryFlag.ImageFor("JP").ShouldNotBeSameAs(CountryFlag.ImageFor(null));
        CountryFlag.ImageFor("jp").ShouldBeSameAs(CountryFlag.ImageFor("JP"));
        CountryFlag.ImageFor("../../US").ShouldBeSameAs(CountryFlag.ImageFor(null));
        CountryFlag.ImageFor("ZZ").ShouldBeSameAs(CountryFlag.ImageFor(null));
        foreach (var path in Directory.GetFiles(Path.Combine(Repo.Root, "shared", "countries", "flags"), "*.png"))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            var image = CountryFlag.ImageFor(name);
            image.PixelSize.Width.ShouldBe(112);
            image.PixelSize.Height.ShouldBe(112);
            if (name != "UNKNOWN")
            {
                image.ShouldNotBeSameAs(CountryFlag.ImageFor(null), name + " должен быть встроен в приложение");
            }
        }

        Snapshot(window, "country-flags-servers");
        var examples = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12, Margin = new Avalonia.Thickness(20) };
        foreach (var code in new string?[] { "US", "JP", "NL", null })
        {
            examples.Children.Add(new CountryFlag { CountryCode = code });
        }

        window.Content = examples;
        Dispatcher.UIThread.RunJobs();
        Snapshot(window, "country-flags-frame");
    }

    [AvaloniaFact]
    public void Changing_country_keeps_flags_and_earth_in_the_same_square_slot()
    {
        var (window, _) = Open();
        var flag = new CountryFlag { CountryCode = "US" };
        window.Content = new StackPanel { Children = { flag } };
        Dispatcher.UIThread.RunJobs();
        var frame = flag.FindControl<Border>("Frame").ShouldNotBeNull();
        frame.Bounds.Size.ShouldBe(new Avalonia.Size(28, 28));
        flag.Bounds.Size.ShouldBe(new Avalonia.Size(28, 28));
        flag.CountryCode = "ZZ";
        Dispatcher.UIThread.RunJobs();
        frame.Bounds.Size.ShouldBe(new Avalonia.Size(28, 28));
        flag.CountryCode = "JP";
        Dispatcher.UIThread.RunJobs();
        frame.Bounds.Size.ShouldBe(new Avalonia.Size(28, 28));
    }

    [AvaloniaFact]
    public async Task Main_flag_requires_a_visible_measured_ip()
    {
        var (window, shell) = Open();
        var main = shell.CurrentPage.ShouldBeOfType<MainPageViewModel>();
        var connecting = shell.Protection.ToggleCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Find<CountryFlag>(window, "WebCountryFlag").IsVisible.ShouldBeFalse();
        Time.Advance(PrivateGateway.Client.Infrastructure.Demo.SimulatedGatewayClient.ConnectDelay);
        await connecting;
        Dispatcher.UIThread.RunJobs();
        Find<CountryFlag>(window, "WebCountryFlag").IsVisible.ShouldBeTrue();
        main.ToggleIpMaskCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Find<CountryFlag>(window, "WebCountryFlag").IsVisible.ShouldBeFalse();
        main.ToggleIpMaskCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Find<CountryFlag>(window, "WebCountryFlag").IsVisible.ShouldBeTrue();
    }

    [AvaloniaFact]
    public void Main_ip_flag_does_not_reuse_the_selected_server_country()
    {
        var (window, shell) = Open();
        var main = shell.CurrentPage.ShouldBeOfType<MainPageViewModel>();
        main.QuickServers.ShouldContain(s => s.CountryCode == "NL");
        // Адреса демо — документационные: стране серверов из названий доверять нельзя.
        main.WebCountryCode.ShouldBeNull();
        main.ToggleIpMaskCommand.Execute(null);
        main.WebCountryCode.ShouldBeNull();
        Snapshot(window, "country-flags-ip-masked");
    }
}
