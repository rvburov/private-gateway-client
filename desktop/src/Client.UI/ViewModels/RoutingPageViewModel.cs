using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// «Маршрутизация» по макету (доска 13): весь трафик или раздельный режим, программы мимо VPN,
/// правила для сайтов. Сохраняется сразу. Мимо VPN идет только то, что выбрано в раздельном
/// режиме: в режиме «Весь трафик» программы и правила «Напрямую» не действуют.
/// </summary>
public sealed partial class RoutingPageViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly OverlayViewModel _overlay;
    private readonly ToastViewModel _toast;
    private readonly IFileOpenService _files;
    private readonly INavigator _navigator;

    public RoutingPageViewModel(
        SettingsService settings, OverlayViewModel overlay, ToastViewModel toast, IFileOpenService files, INavigator navigator)
    {
        _settings = settings;
        _overlay = overlay;
        _toast = toast;
        _files = files;
        _navigator = navigator;
        settings.Changed += (_, _) => Refresh();
        Loc.Instance.LanguageChanged += (_, _) => Refresh();
        Refresh();
    }

    private RoutingSettings R => _settings.Current.Routing;

    // ── Режим ────────────────────────────────────────────────────────────────

    public bool IsAllTraffic
    {
        get => R.Mode == RoutingMode.AllTraffic;
        set => Set(r => r with { Mode = value ? RoutingMode.AllTraffic : RoutingMode.Split });
    }

    public bool IsSplit
    {
        get => R.Mode == RoutingMode.Split;
        set => Set(r => r with { Mode = value ? RoutingMode.Split : RoutingMode.AllTraffic });
    }

    public string ModeNote => IsSplit
        ? Loc.Instance["routing.mode.split.note"]
        : Loc.Instance.Format("routing.mode.all.note", AppInfo.OsName);

    // ── Программы и сайты ────────────────────────────────────────────────────

    public ObservableCollection<BypassAppViewModel> Apps { get; } = [];

    public bool HasApps => Apps.Count > 0;

    public ObservableCollection<SiteRuleViewModel> Rules { get; } = [];

    public bool HasRules => Rules.Count > 0;

    /// <summary>В режиме «Весь трафик» правила «Напрямую» молчат — об этом подпись над списком.</summary>
    public bool HasIdleRules => !IsSplit && R.Rules.Any(r => r.Action == RuleAction.Direct);

    // ── Всегда напрямую ──────────────────────────────────────────────────────

    public string LanText => Loc.Instance[_settings.Current.Protection.AllowLan ? "routing.lan.allowed" : "routing.lan.denied"];

    // ── Команды ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Back() => _navigator.Navigate(Pages.Settings);

    /// <summary>Локальная сеть настраивается на одном экране — «Защита и утечки».</summary>
    [RelayCommand]
    private void OpenProtection() => _navigator.Navigate(Pages.Protection);

    [RelayCommand]
    private async Task AddAppAsync()
    {
        if (await _files.PickProgramAsync().ConfigureAwait(true) is not { } picked)
        {
            return;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (R.Apps.FirstOrDefault(a => string.Equals(a.Path, picked.Path, comparison)) is { } existing)
        {
            // Уже в списке — включить, а не заводить вторую строку.
            SetApp(existing, existing with { IsEnabled = true });
            _toast.Show(Loc.Instance.Format("routing.apps.exists", existing.Name));
            return;
        }

        Set(r => r with { Apps = [.. r.Apps, new BypassApp(picked.Path, picked.Name)] });
    }

    [RelayCommand]
    private void AddRule() => _overlay.ShowSheet(new SiteRuleEditViewModel(null, IsSplit, this, _overlay));

    /// <summary>Заменить программу в списке; null — убрать.</summary>
    internal void SetApp(BypassApp app, BypassApp? replacement) => Set(r => r with
    {
        Apps = [.. r.Apps.Select(a => a == app ? replacement : a).OfType<BypassApp>()],
    });

    internal void EditRule(DomainRule rule) => _overlay.ShowSheet(new SiteRuleEditViewModel(rule, IsSplit, this, _overlay));

    /// <summary>
    /// Новое правило — в конец списка, измененное — на свое место. Правило для того же сайта
    /// заменяется: два правила на один сайт противоречили бы друг другу.
    /// </summary>
    internal void SaveRule(DomainRule? original, DomainRule rule) => Set(r =>
    {
        var rules = r.Rules.Where(other => other == original || other.Pattern != rule.Pattern).ToList();
        var index = original is null ? -1 : rules.IndexOf(original);
        if (index >= 0)
        {
            rules[index] = rule;
        }
        else
        {
            rules.Add(rule);
        }

        return r with { Rules = rules };
    });

    internal void RemoveRule(DomainRule rule) => Set(r => r with { Rules = [.. r.Rules.Where(other => other != rule)] });

    private void Set(Func<RoutingSettings, RoutingSettings> change) =>
        _settings.Update(s => s with { Routing = change(s.Routing) });

    private void Refresh()
    {
        Apps.Clear();
        foreach (var (app, index) in R.Apps.Select((a, i) => (a, i)))
        {
            Apps.Add(new BypassAppViewModel(app, index, this));
        }

        Rules.Clear();
        foreach (var rule in R.Rules)
        {
            Rules.Add(new SiteRuleViewModel(rule, IsSplit, this));
        }

        OnPropertyChanged(string.Empty);
    }

    internal static string ActionLabel(RuleAction action) => Loc.Instance[action switch
    {
        RuleAction.Direct => "routing.action.direct",
        RuleAction.Block => "routing.action.block",
        _ => "routing.action.proxy",
    }];
}

/// <summary>Строка «Программы мимо VPN»: плитка с буквой, имя, сокращенный путь, переключатель.</summary>
public sealed partial class BypassAppViewModel(BypassApp app, int index, RoutingPageViewModel page) : ObservableObject
{
    public string Name => app.Name;

    /// <summary>Первая буква или цифра имени — на цветной плитке.</summary>
    public string Letter
    {
        get
        {
            var c = app.Name.FirstOrDefault(char.IsLetterOrDigit);
            return c == default ? "?" : new string(char.ToUpperInvariant(c), 1);
        }
    }

    /// <summary>Цвет плитки — по месту в списке, три цвета по кругу, как в макете.</summary>
    public bool IsTeal => index % 3 == 1;

    public bool IsAmber => index % 3 == 2;

    /// <summary>Полный путь — в подсказке, в строке — начало и имя файла: «C:\Games\…\game.exe».</summary>
    public string Path => app.Path;

    public string ShortPath => Shorten(app.Path);

    public string SwitchLabel => Loc.Instance.Format("routing.apps.switch", app.Name);

    public string RemoveLabel => Loc.Instance.Format("routing.apps.remove", app.Name);

    public bool IsEnabled
    {
        get => app.IsEnabled;
        set => page.SetApp(app, app with { IsEnabled = value });
    }

    [RelayCommand]
    private void Remove() => page.SetApp(app, null);

    internal static string Shorten(string path)
    {
        var separator = path.Contains('\\', StringComparison.Ordinal) ? '\\' : '/';
        var parts = path.Split(separator);
        return parts.Length <= 4 ? path : string.Join(separator, parts[0], parts[1], "…", parts[^1]);
    }
}

/// <summary>Строка «Правила для сайтов»: шаблон и метка действия; нажатие — лист правки.</summary>
public sealed partial class SiteRuleViewModel(DomainRule rule, bool isSplit, RoutingPageViewModel page) : ObservableObject
{
    public string Pattern => rule.Pattern;

    public string ActionText => RoutingPageViewModel.ActionLabel(rule.Action);

    public bool IsDirect => rule.Action == RuleAction.Direct;

    public bool IsProxy => rule.Action == RuleAction.Proxy;

    public bool IsBlock => rule.Action == RuleAction.Block;

    /// <summary>«Напрямую» вне раздельного режима не действует — метка бледнее.</summary>
    public bool IsIdle => IsDirect && !isSplit;

    public string EditLabel => Loc.Instance.Format("routing.rule.editLabel", rule.Pattern, ActionText);

    [RelayCommand]
    private void Edit() => page.EditRule(rule);
}

/// <summary>
/// Лист правила для сайта: шаблон и действие. Новое правило в раздельном режиме — «Напрямую»
/// (ради этого его и заводят), в режиме «Весь трафик» — «Блокировать»: «Напрямую» там не действует.
/// </summary>
public sealed partial class SiteRuleEditViewModel : ObservableObject
{
    private readonly DomainRule? _original;
    private readonly bool _isSplit;
    private readonly RoutingPageViewModel _page;
    private readonly OverlayViewModel _overlay;

    public SiteRuleEditViewModel(DomainRule? original, bool isSplit, RoutingPageViewModel page, OverlayViewModel overlay)
    {
        _original = original;
        _isSplit = isSplit;
        _page = page;
        _overlay = overlay;
        Pattern = original?.Pattern ?? string.Empty;
        Action = original?.Action ?? (isSplit ? RuleAction.Direct : RuleAction.Block);
    }

    public bool IsNew => _original is null;

    public string Title => Loc.Instance[IsNew ? "routing.rule.new" : "routing.rule.edit"];

    public string SaveText => Loc.Instance[IsNew ? "routing.rule.add" : "routing.rule.save"];

    [ObservableProperty]
    public partial string Pattern { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDirect), nameof(IsProxy), nameof(IsBlock), nameof(IsIdle))]
    public partial RuleAction Action { get; set; }

    public bool IsDirect
    {
        get => Action == RuleAction.Direct;
        set => Choose(value, RuleAction.Direct);
    }

    public bool IsProxy
    {
        get => Action == RuleAction.Proxy;
        set => Choose(value, RuleAction.Proxy);
    }

    public bool IsBlock
    {
        get => Action == RuleAction.Block;
        set => Choose(value, RuleAction.Block);
    }

    /// <summary>«Напрямую» вне раздельного режима — подпись, что правило пока не действует.</summary>
    public bool IsIdle => IsDirect && !_isSplit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [RelayCommand]
    private void Save()
    {
        if (SitePattern.Normalize(Pattern) is not { } pattern)
        {
            Error = Loc.Instance["routing.rule.bad"];
            return;
        }

        _overlay.CloseSheet();
        _page.SaveRule(_original, new DomainRule(pattern, Action));
    }

    [RelayCommand]
    private void Delete()
    {
        _overlay.CloseSheet();
        if (_original is not null)
        {
            _page.RemoveRule(_original);
        }
    }

    [RelayCommand]
    private void Cancel() => _overlay.CloseSheet();

    private void Choose(bool chosen, RuleAction action)
    {
        if (chosen)
        {
            Action = action;
        }
    }
}
