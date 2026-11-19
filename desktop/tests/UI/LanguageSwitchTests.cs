using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// Язык меняется сразу на всех экранах: страница, открытая на одном языке, после смены не оставляет подписей прежнего
/// словаря. Названия серверов и списков — данные пользователя, их язык не меняет, поэтому сверка — со словарем.
/// </summary>
public sealed class LanguageSwitchTests : WindowHarness
{
    [AvaloniaTheory]
    [InlineData("ru", "en", false)]
    [InlineData("ru", "en", true)]
    [InlineData("en", "ru", false)]
    [InlineData("en", "ru", true)]
    public async Task Every_page_follows_the_language_at_once(string from, string to, bool connected)
    {
        var before = Dictionary(from);
        var after = Dictionary(to);
        // Подпись прежнего словаря, которая в новом другая: ее появление после смены — ошибка. «{0}» — любым текстом.
        var patterns = before.Where(p => after.GetValueOrDefault(p.Key) != p.Value && Regex.IsMatch(p.Value, from == "ru" ? "[А-Яа-я]" : "[A-Za-z]{3}"))
            .Select(p => new Regex("^" + Regex.Replace(Regex.Escape(p.Value), @"\\\{\d+}", ".*") + "$", RegexOptions.Singleline))
            .ToList();
        var (window, shell) = Open();
        if (connected)
        {
            await Connect(shell);
        }

        var left = new List<string>();
        foreach (var page in typeof(Pages).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!))
        {
            Loc.Instance.SetLanguage(from);
            shell.Navigate(page);
            Dispatcher.UIThread.RunJobs();
            Loc.Instance.SetLanguage(to);
            Dispatcher.UIThread.RunJobs();
            left.AddRange(window.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible && t.Text is { Length: > 0 } text && !SystemNames.Contains(text) && patterns.Any(p => p.IsMatch(text)))
                .Select(t => $"{shell.CurrentKey}: «{t.Text}»"));
        }

        left.Distinct().ShouldBeEmpty();
    }

    // Имена сетевых адаптеров имитации — данные системы, а не подписи, хоть и совпадают с английскими словами словаря.
    private static readonly HashSet<string> SystemNames = ["Ethernet", "Loopback"];

    private static Dictionary<string, string> Dictionary(string language) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(Repo.Root, "shared", "specifications", "localization", language + ".json")))!;
}
