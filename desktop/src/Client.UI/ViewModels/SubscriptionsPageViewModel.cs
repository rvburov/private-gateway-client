using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// «Профили и подписки» по макету (доска 12): автообновление и две настройки сверху, ниже —
/// карточки групп с «Обновить», «Изменить» и удалением. Список — только свои серверы
/// (<see cref="IServerStore"/>): демо-серверы здесь не показываются.
/// </summary>
public sealed partial class SubscriptionsPageViewModel : ObservableObject
{
    private readonly IServerStore _store;
    private readonly SubscriptionUpdater _updater;
    private readonly SettingsService _settings;
    private readonly ConnectionController _controller;
    private readonly IGatewayClient _gateway;
    private readonly OverlayViewModel _overlay;
    private readonly ToastViewModel _toast;
    private readonly INavigator _navigator;
    private readonly TimeProvider _time;

    public SubscriptionsPageViewModel(
        IServerStore store,
        SubscriptionUpdater updater,
        SettingsService settings,
        ConnectionController controller,
        IGatewayClient gateway,
        OverlayViewModel overlay,
        ToastViewModel toast,
        INavigator navigator,
        TimeProvider time)
    {
        _store = store;
        _updater = updater;
        _settings = settings;
        _controller = controller;
        _gateway = gateway;
        _overlay = overlay;
        _toast = toast;
        _navigator = navigator;
        _time = time;
        RefreshOptions = [.. Enum.GetValues<SubscriptionRefresh>().Select(v => new RefreshOptionViewModel(v, this))];
        store.Changed += (_, _) => Dispatcher.UIThread.Post(Rebuild);
        updater.BusyChanged += (_, _) => Dispatcher.UIThread.Post(Rebuild);
        gateway.StatusChanged += (_, _) => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(BlockNote)));
        settings.Changed += (_, _) => Rebuild();
        Loc.Instance.LanguageChanged += (_, _) => Rebuild();
        Rebuild();
    }

    public ObservableCollection<SubscriptionCardViewModel> Cards { get; } = [];

    /// <summary>«ПОДПИСКИ · 4» — рубрика над карточками.</summary>
    public string SectionTitle => Loc.Instance.Format("subs.section", Cards.Count).ToUpper(Loc.Instance.Culture);

    public bool IsEmpty => Cards.Count == 0;

    // ── Настройки ────────────────────────────────────────────────────────────

    public IReadOnlyList<RefreshOptionViewModel> RefreshOptions { get; }

    /// <summary>«каждые 12 ч» на кнопке выбора.</summary>
    public string RefreshText => RefreshLabel(_settings.Current.SubscriptionRefresh);

    [ObservableProperty]
    public partial bool IsRefreshOpen { get; set; }

    public bool UpdateOnlyViaVpn
    {
        get => _settings.Current.UpdateOnlyViaVpn;
        set => _settings.Update(s => s with { UpdateOnlyViaVpn = value });
    }

    public bool NotifySelectedServerChange
    {
        get => _settings.Current.NotifySelectedServerChange;
        set => _settings.Update(s => s with { NotifySelectedServerChange = value });
    }

    /// <summary>Почему подписки сейчас не обновляются — под настройкой «только через VPN».</summary>
    public string? BlockNote => _updater.Block switch
    {
        UpdateBlock.ProtectionOff => Loc.Instance["subs.vpn.blocked.off"],
        UpdateBlock.ServiceMissing => Loc.Instance["subs.vpn.blocked.service"],
        _ => null,
    };

    internal static string RefreshLabel(SubscriptionRefresh value) => Loc.Instance[value switch
    {
        SubscriptionRefresh.Every6Hours => "subs.refresh.6",
        SubscriptionRefresh.Every12Hours => "subs.refresh.12",
        SubscriptionRefresh.Every24Hours => "subs.refresh.24",
        _ => "subs.refresh.off",
    }];

    internal bool IsCurrentRefresh(SubscriptionRefresh value) => _settings.Current.SubscriptionRefresh == value;

    internal void ChooseRefresh(SubscriptionRefresh value)
    {
        IsRefreshOpen = false;
        _settings.Update(s => s with { SubscriptionRefresh = value });
    }

    // ── Команды ──────────────────────────────────────────────────────────────

    [RelayCommand]
    private void Back() => _navigator.Navigate(Pages.Settings);

    [RelayCommand]
    private void Add() => _navigator.Navigate(Pages.Add);

    [RelayCommand]
    private void ToggleRefresh() => IsRefreshOpen = !IsRefreshOpen;

    /// <summary>«Обновить» у карточки: итог — уведомлением, ошибка остается и в карточке.</summary>
    internal async Task UpdateAsync(string subscriptionId)
    {
        var name = _store.Subscriptions.FirstOrDefault(s => s.Id == subscriptionId)?.Name ?? string.Empty;
        var result = await _updater.UpdateAsync(subscriptionId).ConfigureAwait(true);
        switch (result)
        {
            case { Block: UpdateBlock.ProtectionOff }:
                _toast.Show(Loc.Instance["subs.blocked.off"], ToastViewModel.LongDuration);
                break;
            case { Block: UpdateBlock.ServiceMissing }:
                _toast.Show(Loc.Instance["subs.blocked.service"], ToastViewModel.LongDuration);
                break;
            case { Update: { } update }:
                _toast.Show(Loc.Instance.Format("subs.updated", name, update.Added, update.Removed));
                break;
            case { Failure: not null }:
                _toast.Show(Loc.Instance.Format("subs.failed", name));
                break;
        }
    }

    internal void Edit(ServerSubscription subscription) =>
        _overlay.ShowSheet(new SubscriptionEditViewModel(subscription, _store, this, _overlay));

    /// <summary>Удалить группу с подтверждением; если защита шла через ее сервер — сначала отключиться.</summary>
    internal async Task RemoveAsync(string? subscriptionId, string name)
    {
        var ids = _store.Servers.Where(s => s.SubscriptionId == subscriptionId).Select(s => s.Id).ToList();
        var status = _gateway.Status;
        var inUse = status.ServerId is { } id && ids.Contains(id) && status.State != ConnectionState.Off;
        var dialog = new DialogViewModel(
            DialogKind.Danger,
            "Icon.Trash",
            null,
            Loc.Instance.Format("subs.remove.title", name),
            Loc.Instance.Format(inUse ? "subs.remove.textInUse" : "subs.remove.text", Loc.Instance.Plural("subs.servers", ids.Count)),
            Loc.Instance["dialog.deleteServer.delete"],
            Loc.Instance["dialog.cancel"]);
        if (await _overlay.AskAsync(dialog).ConfigureAwait(true) != DialogChoice.Primary)
        {
            return;
        }

        await _controller.ReleaseAsync(ids).ConfigureAwait(true);
        _store.RemoveSubscription(subscriptionId);
        _toast.Show(Loc.Instance.Format("subs.removed", name));
    }

    private void Rebuild()
    {
        var servers = _store.Servers;
        Cards.Clear();
        foreach (var subscription in _store.Subscriptions)
        {
            Cards.Add(SubscriptionCardViewModel.For(subscription, servers.Count(s => s.SubscriptionId == subscription.Id),
                _updater.IsUpdating(subscription.Id), _time, this));
        }

        if (servers.Count(s => s.SubscriptionId is null) is > 0 and var mine)
        {
            Cards.Add(SubscriptionCardViewModel.Mine(mine, this));
        }

        foreach (var option in RefreshOptions)
        {
            option.Refresh();
        }

        OnPropertyChanged(string.Empty);
    }
}

/// <summary>Вариант автообновления во всплывающем списке.</summary>
public sealed partial class RefreshOptionViewModel(SubscriptionRefresh value, SubscriptionsPageViewModel page) : ObservableObject
{
    public string Label => SubscriptionsPageViewModel.RefreshLabel(value);

    public bool IsCurrent => page.IsCurrentRefresh(value);

    internal void Refresh() => OnPropertyChanged(string.Empty);

    [RelayCommand]
    private void Choose() => page.ChooseRefresh(value);
}

/// <summary>Чем помечена группа: работает, последняя попытка не удалась, без адреса.</summary>
public enum SubscriptionHealth
{
    Ok,
    Error,
    Manual,
}

/// <summary>
/// Карточка группы: название, подпись адреса без токена, сколько серверов и когда обновлялась,
/// метка состояния, ошибка последней попытки и кнопки.
/// </summary>
public sealed partial class SubscriptionCardViewModel : ObservableObject
{
    private readonly ServerSubscription? _subscription;
    private readonly SubscriptionsPageViewModel _page;

    private SubscriptionCardViewModel(
        ServerSubscription? subscription, string name, string address, string meta, SubscriptionHealth health,
        string? error, bool isUpdating, SubscriptionsPageViewModel page)
    {
        _subscription = subscription;
        _page = page;
        Name = name;
        Address = address;
        Meta = meta;
        Health = health;
        Error = error;
        IsUpdating = isUpdating;
    }

    /// <summary>null — «Мои серверы»: серверы, добавленные ссылками без названия.</summary>
    public string? Id => _subscription?.Id;

    public string Name { get; }

    public string Address { get; }

    public string Meta { get; }

    public SubscriptionHealth Health { get; }

    public bool IsOk => Health == SubscriptionHealth.Ok;

    public bool IsError => Health == SubscriptionHealth.Error;

    public bool IsManual => Health == SubscriptionHealth.Manual;

    public string HealthText => Loc.Instance[Health switch
    {
        SubscriptionHealth.Ok => "subs.chip.ok",
        SubscriptionHealth.Error => "subs.chip.error",
        _ => "subs.chip.manual",
    }];

    /// <summary>«Сервер ответил 403 — доступ запрещен, проверьте ссылку. Оставлена прошлая версия списка…»</summary>
    public string? Error { get; }

    public bool HasError => Error is not null;

    public bool CanUpdate => _subscription?.CanUpdate == true;

    public bool IsUpdating { get; }

    /// <summary>«Мои серверы» не переименовать: это не группа, а серверы без группы.</summary>
    public bool CanEdit => _subscription is not null;

    public string RemoveLabel => Loc.Instance.Format("subs.remove", Name);

    internal static SubscriptionCardViewModel For(
        ServerSubscription subscription, int servers, bool isUpdating, TimeProvider time, SubscriptionsPageViewModel page)
    {
        var count = Loc.Instance.Plural("subs.servers", servers);
        if (!subscription.CanUpdate)
        {
            return new(subscription, subscription.Name, Loc.Instance["subs.manual.address"], count, SubscriptionHealth.Manual, null, false, page);
        }

        if (subscription.LastFailure is { } failure)
        {
            var meta = subscription.LastAttemptAt is { } attempt
                ? Loc.Instance.Format("subs.meta.attempt", count, TimeText.Ago(attempt, time))
                : count;
            var error = Loc.Instance.Format("subs.error", FailureText.Sentence(failure, subscription.LastStatusCode));
            return new(subscription, subscription.Name, subscription.Address!, meta, SubscriptionHealth.Error, error, isUpdating, page);
        }

        var updated = subscription.UpdatedAt is { } at
            ? Loc.Instance.Format("subs.meta.updated", count, TimeText.Day(at, time))
            : count;
        return new(subscription, subscription.Name, subscription.Address!, updated, SubscriptionHealth.Ok, null, isUpdating, page);
    }

    internal static SubscriptionCardViewModel Mine(int servers, SubscriptionsPageViewModel page) =>
        new(null, Loc.Instance["subs.mine.name"], Loc.Instance["subs.mine.address"], Loc.Instance.Plural("subs.servers", servers),
            SubscriptionHealth.Manual, null, false, page);

    [RelayCommand]
    private Task UpdateAsync() => _subscription is { } s ? _page.UpdateAsync(s.Id) : Task.CompletedTask;

    [RelayCommand]
    private void Edit()
    {
        if (_subscription is { } s)
        {
            _page.Edit(s);
        }
    }

    [RelayCommand]
    private Task RemoveAsync() => _page.RemoveAsync(Id, Name);
}

/// <summary>
/// Лист «Изменить»: название и новый адрес подписки. Пустой адрес — оставить прежний; новый —
/// только в хранилище ОС, и подписка сразу обновляется.
/// </summary>
public sealed partial class SubscriptionEditViewModel : ObservableObject
{
    private readonly ServerSubscription _subscription;
    private readonly IServerStore _store;
    private readonly SubscriptionsPageViewModel _page;
    private readonly OverlayViewModel _overlay;

    public SubscriptionEditViewModel(
        ServerSubscription subscription, IServerStore store, SubscriptionsPageViewModel page, OverlayViewModel overlay)
    {
        _subscription = subscription;
        _store = store;
        _page = page;
        _overlay = overlay;
        Name = subscription.Name;
    }

    public string Title => Loc.Instance.Format("subs.edit.title", _subscription.Name);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Url { get; set; } = string.Empty;

    /// <summary>Прежний адрес без токена — подсказкой в пустом поле.</summary>
    public string UrlPlaceholder => _subscription.Address ?? "https://…";

    public string UrlNote => Loc.Instance.Format("subs.edit.url.note", _store.SecretStoreName);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    private bool CanSave => Name.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Uri? url = null;
        var text = Url.Trim();
        if (text.Length > 0
            && (!Uri.TryCreate(text, UriKind.Absolute, out url) || url.Scheme is not ("https" or "http")))
        {
            Error = Loc.Instance["subs.edit.url.bad"];
            return;
        }

        try
        {
            _store.EditSubscription(_subscription.Id, Name, url);
        }
        catch (InvalidOperationException)
        {
            Error = Loc.Instance.Format("import.store.unavailable", _store.SecretStoreName);
            return;
        }

        _overlay.CloseSheet();
        if (url is not null)
        {
            await _page.UpdateAsync(_subscription.Id).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void Cancel() => _overlay.CloseSheet();
}
