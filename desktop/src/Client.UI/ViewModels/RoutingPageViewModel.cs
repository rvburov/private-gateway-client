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
    private readonly BlockPinGate _pin;

    public RoutingPageViewModel(
        SettingsService settings, OverlayViewModel overlay, ToastViewModel toast, IFileOpenService files, INavigator navigator, BlockPinGate pin)
    {
        _settings = settings;
        _pin = pin;
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

    /// <summary>
    /// «Раздельный режим» — только через «Перейти» в диалоге (просьба пользователя 2026-11-05): сегмент сам не отмечается
    /// (<see cref="Controls.ConfirmRadioButton"/>), до ответа остается «Весь трафик». Обратно на «Весь трафик» — без вопроса.
    /// </summary>
    public bool IsSplit => R.Mode == RoutingMode.Split;

    [RelayCommand]
    private async Task ChooseSplitAsync()
    {
        if (R.Mode != RoutingMode.Split && await SafetyDialogs.ConfirmSplitModeAsync(_overlay).ConfigureAwait(true))
        {
            Set(r => r with { Mode = RoutingMode.Split });
        }
    }

    public string ModeNote => IsSplit
        ? Loc.Instance["routing.mode.split.note"]
        : Loc.Instance.Format("routing.mode.all.note", AppInfo.OsName);

    // ── Программы и сайты ────────────────────────────────────────────────────

    public ObservableCollection<BypassAppViewModel> Apps { get; } = [];

    public bool HasApps => Apps.Count > 0;

    public ObservableCollection<SiteRuleViewModel> Rules { get; } = [];

    public bool HasRules => Rules.Count > 0;

    /// <summary>
    /// В режиме «Весь трафик» правила «Напрямую» молчат — об этом подпись над списком. При «Остальные сайты: Блокировать» они
    /// разрешают сайт через VPN (решение 740) и не молчат.
    /// </summary>
    public bool HasIdleRules => DirectIdle && R.Rules.Any(r => r.Action == RuleAction.Direct);

    /// <summary>«Напрямую» не действует: не раздельный режим и остальное не блокируется.</summary>
    private bool DirectIdle => !IsSplit && !R.BlockOthers;

    // ── Остальные сайты (решение 740) ────────────────────────────────────────

    /// <summary>«Остальные сайты: Открывать» — без вопроса; с PIN-кодом — после кода (решение 742).</summary>
    public bool OthersOpen => !R.BlockOthers;

    [RelayCommand]
    private async Task ChooseOpenOthersAsync()
    {
        if (R.BlockOthers
            && await _pin.AllowAsync(Loc.Instance["block.guard.others.title"], Loc.Instance["block.guard.others.text"]).ConfigureAwait(true))
        {
            Set(r => r with { BlockOthers = false });
        }
    }

    // ── Блок сайтов: PIN-код (решение 742) ───────────────────────────────────

    public bool HasPin => _settings.Current.BlockPin is not null;

    public string PinText => Loc.Instance[HasPin ? "block.pin.on" : "block.pin.off"];

    public string PinButtonText => Loc.Instance[HasPin ? "block.pin.changeButton" : "block.pin.setButton"];

    [RelayCommand]
    private Task ChangePinAsync() => _pin.ChangeAsync();

    /// <summary>«Блокировать» — только через «Блокировать остальное» в диалоге: сегмент сам не отмечается.</summary>
    public bool BlockOthers => R.BlockOthers;

    [RelayCommand]
    private async Task ChooseBlockOthersAsync()
    {
        if (!R.BlockOthers && await SafetyDialogs.ConfirmBlockOthersAsync(_overlay).ConfigureAwait(true))
        {
            Set(r => r with { BlockOthers = true });
        }
    }

    public string OthersNote => Loc.Instance[R.BlockOthers ? "routing.others.block.note" : "routing.others.open.note"];

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
    private void AddRule() => _overlay.ShowSheet(new SiteRuleEditViewModel(null, IsSplit, R.BlockOthers, this, _overlay));

    /// <summary>Заменить программу в списке; null — убрать.</summary>
    internal void SetApp(BypassApp app, BypassApp? replacement) => Set(r => r with
    {
        Apps = [.. r.Apps.Select(a => a == app ? replacement : a).OfType<BypassApp>()],
    });

    internal void EditRule(DomainRule rule) => _overlay.ShowSheet(new SiteRuleEditViewModel(rule, IsSplit, R.BlockOthers, this, _overlay));

    /// <summary>
    /// Новое правило — в конец списка, измененное — на свое место. Правило для того же сайта
    /// заменяется: два правила на один сайт противоречили бы друг другу. «Только этот адрес» и сайт
    /// с поддоменами — разные правила (решение 739).
    /// </summary>
    /// <summary>
    /// Ослабляет ли правка блок (решение 742): меняется или пропадает правило «Блокировать», а при «Остальные сайты:
    /// Блокировать» — появляется или меняет сайт разрешение. Тогда сначала PIN-код.
    /// </summary>
    internal async Task SaveRuleAsync(DomainRule? original, DomainRule rule)
    {
        // Новое разрешение или разрешение для другого сайта, когда остальные заблокированы.
        var allowsMore = R.BlockOthers && rule.Action != RuleAction.Block
            && (original is null || original.Action == RuleAction.Block || original.Pattern != rule.Pattern || original.IsExact != rule.IsExact);
        var (title, text) = original is { Action: RuleAction.Block } && original != rule
            ? (Loc.Instance.Format("block.guard.rule.title", original.Pattern), Loc.Instance["block.guard.rule.text"])
            : allowsMore
                ? (Loc.Instance.Format("block.guard.allow.title", rule.Pattern), Loc.Instance["block.guard.allow.text"])
                : (null, null);
        if (title is null || await _pin.AllowAsync(title, text!).ConfigureAwait(true))
        {
            SaveRule(original, rule);
        }
    }

    internal async Task RemoveRuleAsync(DomainRule rule)
    {
        if (rule.Action != RuleAction.Block
            || await _pin.AllowAsync(Loc.Instance.Format("block.guard.rule.title", rule.Pattern), Loc.Instance["block.guard.rule.text"]).ConfigureAwait(true))
        {
            RemoveRule(rule);
        }
    }

    internal void SaveRule(DomainRule? original, DomainRule rule) => Set(r =>
    {
        var rules = r.Rules.Where(other => other == original || other.Pattern != rule.Pattern || other.IsExact != rule.IsExact).ToList();
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
            Rules.Add(new SiteRuleViewModel(rule, DirectIdle, this));
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
public sealed partial class SiteRuleViewModel(DomainRule rule, bool directIdle, RoutingPageViewModel page) : ObservableObject
{
    public string Pattern => rule.Pattern;

    /// <summary>«Только этот адрес» — подписью под адресом (решение 739).</summary>
    public bool IsExact => rule.IsExact;

    /// <summary>«geosite:id» сервиса из списка — подпись «все адреса YouTube» (решение 741).</summary>
    public string? ServiceText => ServiceSites.OfPattern(rule.Pattern) is { } service
        ? Loc.Instance.Format("routing.rule.service.row", service.Name)
        : null;

    public bool HasService => ServiceText is not null;

    public string ActionText => RoutingPageViewModel.ActionLabel(rule.Action);

    public bool IsDirect => rule.Action == RuleAction.Direct;

    public bool IsProxy => rule.Action == RuleAction.Proxy;

    public bool IsBlock => rule.Action == RuleAction.Block;

    /// <summary>«Напрямую» вне раздельного режима не действует — метка бледнее (кроме «Остальные сайты: Блокировать»).</summary>
    public bool IsIdle => IsDirect && directIdle;

    public string EditLabel => Loc.Instance.Format("routing.rule.editLabel", rule.Pattern, ActionText);

    [RelayCommand]
    private void Edit() => page.EditRule(rule);
}

/// <summary>
/// Лист правила для сайта: шаблон и действие. Новое правило в раздельном режиме — «Напрямую»
/// (ради этого его и заводят), в режиме «Весь трафик» — «Блокировать»: «Напрямую» там не действует;
/// при «Остальные сайты: Блокировать» — «Через VPN»: правило разрешает сайт (решение 740).
/// </summary>
public sealed partial class SiteRuleEditViewModel : ObservableObject
{
    private readonly DomainRule? _original;
    private readonly bool _directIdle;
    private readonly RoutingPageViewModel _page;
    private readonly OverlayViewModel _overlay;

    public SiteRuleEditViewModel(DomainRule? original, bool isSplit, bool blockOthers, RoutingPageViewModel page, OverlayViewModel overlay)
    {
        _original = original;
        _directIdle = !isSplit && !blockOthers;
        _page = page;
        _overlay = overlay;
        Pattern = original?.Pattern ?? string.Empty;
        Action = original?.Action ?? (isSplit ? RuleAction.Direct : blockOthers ? RuleAction.Proxy : RuleAction.Block);
        Exact = original?.IsExact ?? false;
        // «Все адреса сервиса» у нового правила — сразу: без них сайт сервиса не работает (решение 741).
        AllAddresses = original is null;
    }

    public bool IsNew => _original is null;

    public string Title => Loc.Instance[IsNew ? "routing.rule.new" : "routing.rule.edit"];

    public string SaveText => Loc.Instance[IsNew ? "routing.rule.add" : "routing.rule.save"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExact), nameof(Service), nameof(CanAllAddresses), nameof(AllAddressesText))]
    public partial string Pattern { get; set; }

    /// <summary>«Только этот адрес» (решение 739): флажок есть только у обычного домена — не у зоны, списка и адреса.</summary>
    [ObservableProperty]
    public partial bool Exact { get; set; }

    public bool CanExact => SitePattern.Normalize(Pattern) is { } pattern && SitePattern.IsPlainDomain(pattern) && !UsesService;

    /// <summary>
    /// «Все адреса сервиса» (решение 741): флажок есть, когда домен входит в адреса сервиса из списка (youtube.com — YouTube);
    /// отмечен — правило «geosite:id» для всех адресов сервиса, «Только этот адрес» тогда не действует.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExact))]
    public partial bool AllAddresses { get; set; }

    public ServiceSite? Service => SitePattern.Normalize(Pattern) is { } pattern ? ServiceSites.Find(pattern) : null;

    public bool CanAllAddresses => Service is not null;

    public string AllAddressesText => Service is { } service ? Loc.Instance.Format("routing.rule.service", service.Name) : string.Empty;

    private bool UsesService => AllAddresses && Service is not null;

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
    public bool IsIdle => IsDirect && _directIdle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (SitePattern.Normalize(Pattern) is not { } pattern)
        {
            Error = Loc.Instance["routing.rule.bad"];
            return;
        }

        var rule = ServiceSites.Find(pattern) is { } service && AllAddresses
            ? new DomainRule(service.Pattern, Action)
            : new DomainRule(pattern, Action, Exact && SitePattern.IsPlainDomain(pattern));
        _overlay.CloseSheet();
        await _page.SaveRuleAsync(_original, rule).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        _overlay.CloseSheet();
        if (_original is not null)
        {
            await _page.RemoveRuleAsync(_original).ConfigureAwait(true);
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
