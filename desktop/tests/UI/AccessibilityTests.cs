using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// Доступность: у каждого элемента управления на каждом экране есть имя для читалки экрана — подпись
/// текстом или AutomationProperties.Name. Кнопка-значок без имени для незрячего — пустое место.
/// </summary>
public sealed class AccessibilityTests : WindowHarness
{
    public static TheoryData<string> AllPages =>
    [
        Pages.Home, Pages.Servers, Pages.Check, Pages.Settings, Pages.Add, Pages.Protection, Pages.Core, Pages.Routing,
        Pages.Profiles, Pages.Backup, Pages.About,
    ];

    [AvaloniaTheory]
    [MemberData(nameof(AllPages))]
    public void Every_control_has_a_name_for_screen_readers(string page)
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(page);
        Dispatcher.UIThread.RunJobs();

        Unnamed(window).ShouldBeEmpty($"на экране «{page}» у этих элементов нет имени для читалки экрана");
    }

    [AvaloniaFact]
    public void First_launch_controls_have_names()
    {
        var (window, _) = Open(firstLaunch: true);

        Unnamed(window).ShouldBeEmpty();
    }

    [AvaloniaFact]
    public void Sheets_and_dialogs_have_names_too()
    {
        var (window, shell) = Open();
        var checks = new (string Page, Action Open)[]
        {
            (Pages.Servers, () => ((ServersPageViewModel)shell.CurrentPage!).OpenCardCommand.Execute("demo-nl-01")),
            (Pages.Protection, () => ((ProtectionPageViewModel)shell.CurrentPage!).DnsOptions[^1].ChooseCommand.Execute(null)),
            (Pages.Core, () => ((CorePageViewModel)shell.CurrentPage!).MtuOptions[^1].ChooseCommand.Execute(null)),
            (Pages.Routing, () => ((RoutingPageViewModel)shell.CurrentPage!).AddRuleCommand.Execute(null)),
            (Pages.About, () => ((AboutPageViewModel)shell.CurrentPage!).OpenLicensesCommand.Execute(null)),
            (Pages.Settings, () => ((SettingsPageViewModel)shell.CurrentPage!).KillSwitch = false),
        };

        foreach (var (page, open) in checks)
        {
            shell.NavigateCommand.Execute(page);
            Dispatcher.UIThread.RunJobs();
            open();
            Dispatcher.UIThread.RunJobs();
            (shell.Overlay.HasSheet || shell.Overlay.HasDialog).ShouldBeTrue(page);

            Unnamed(window).ShouldBeEmpty($"лист или диалог на экране «{page}»");
            shell.Overlay.DismissCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Видимые и доступные элементы управления без имени: тип, x:Name и место в дереве.</summary>
    internal static List<string> Unnamed(MainWindow window) =>
    [
        .. window.GetVisualDescendants()
            .OfType<Control>()
            .Where(c => c is Button or ToggleButton or TextBox or Slider or ComboBox && c.IsEffectivelyVisible && c.IsEffectivelyEnabled)
            .Where(c => string.IsNullOrWhiteSpace(Name(c)))
            .Select(c => $"{c.GetType().Name} {c.Name} в {c.GetVisualParent()?.GetType().Name}"),
    ];

    private static string? Name(Control control)
    {
        if (AutomationProperties.GetName(control) is { Length: > 0 } name)
        {
            return name;
        }

        if (AutomationProperties.GetLabeledBy(control) is { } label)
        {
            return label is TextBlock t ? t.Text : label.Name;
        }

        return control switch
        {
            ContentControl { Content: string text } => text,
            ContentControl content => content.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t)),
            _ => null,
        };
    }
}
