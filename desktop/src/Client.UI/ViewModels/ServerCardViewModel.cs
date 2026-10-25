using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// Карточка сервера (доска 11) — нижний лист по «⋯» в списке: избранное, пинг, сведения о
/// подключении, «Подключиться», переименование, «Поделиться» (QR-код и ссылка с ключом) и удаление
/// с подтверждением.
/// </summary>
public sealed partial class ServerCardViewModel : ObservableObject, IDisposable
{
    /// <summary>Длина своего названия сервера.</summary>
    public const int MaxNameLength = 64;

    private readonly IServerCatalog _catalog;
    private readonly IServerStore _store;
    private readonly IClipboardService _clipboard;
    private readonly ProtectionViewModel _protection;
    private readonly ConnectionController _controller;
    private readonly OverlayViewModel _overlay;
    private readonly ToastViewModel _toast;
    private ServerProfile _server;

    // Ссылка с ключом — только пока открыт QR-код.
    private string? _link;

    public ServerCardViewModel(
        string serverId,
        IServerCatalog catalog,
        IServerStore store,
        IClipboardService clipboard,
        ProtectionViewModel protection,
        ConnectionController controller,
        OverlayViewModel overlay,
        ToastViewModel toast)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(protection);
        ArgumentNullException.ThrowIfNull(store);
        _catalog = catalog;
        _store = store;
        _clipboard = clipboard;
        _protection = protection;
        _controller = controller;
        _overlay = overlay;
        _toast = toast;
        _server = catalog.Servers.First(s => s.Id == serverId);
        catalog.Changed += OnCatalogChanged;
        protection.PropertyChanged += OnProtectionChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    public string Id => _server.Id;

    public string CountryCode => _server.CountryCode;

    public string Name => _server.Name;

    /// <summary>«Амстердам · из подписки «Основная»»; без подписки — «добавлен ссылкой».</summary>
    public string Subtitle
    {
        get
        {
            var source = _server.MissingSince is not null ? Loc.Instance["card.missing"]
                : _catalog.Subscriptions.FirstOrDefault(s => s.Id == _server.SubscriptionId) is { } subscription
                    ? Loc.Instance.Format("card.from.subscription", subscription.Name)
                    : Loc.Instance["card.from.link"];
            return string.IsNullOrEmpty(_server.City) ? source : _server.City + " · " + source;
        }
    }

    // ── Избранное ────────────────────────────────────────────────────────────

    public bool IsFavorite => _server.IsFavorite;

    public string FavoriteHint => Loc.Instance[IsFavorite ? "card.favorite.remove" : "card.favorite.add"];

    [RelayCommand]
    private void ToggleFavorite() => _catalog.SetFavorite(Id, !IsFavorite);

    // ── Состояние и пинг ─────────────────────────────────────────────────────

    private bool IsSelected => _catalog.Selected?.Id == Id;

    /// <summary>Защита идет через этот сервер или подключается к нему.</summary>
    public bool IsInUse => IsSelected && !ConnectionController.WantsConnect(_protection.Status.State);

    /// <summary>«Активен» — защита через него; «Выбран» — к нему подключит кнопка-щит.</summary>
    public string? StatusText => IsInUse
        ? Loc.Instance["card.status.active"]
        : IsSelected ? Loc.Instance["card.status.selected"] : null;

    public bool HasStatus => StatusText is not null;

    public string LatencyText => _server.LatencyMs is { } ms
        ? ms.ToString(CultureInfo.InvariantCulture) + " " + Loc.Instance["unit.ms"]
        : Loc.Instance["card.latency.none"];

    public bool IsSlow => _server.LatencyMs > ServersPageViewModel.SlowLatencyMs;

    public bool IsDown => _server.LatencyMs is null;

    // ── Сведения ─────────────────────────────────────────────────────────────

    public IReadOnlyList<CardDetail> Details
    {
        get
        {
            var details = new List<CardDetail>
            {
                new(Loc.Instance["card.address"], FormatAddress(_server.Host, _server.Port)),
                new(Loc.Instance["card.protocol"], ServerFacts.ProtocolName(_server.Protocol)),
                new(Loc.Instance["card.transport"], ServerFacts.TransportName(_server.Transport) ?? _server.Transport.ToUpperInvariant()),
                new(Loc.Instance["card.security"], ServerFacts.SecurityName(_server.Security)
                    ?? Loc.Instance[ServerFacts.IsEncrypted(_server) ? "card.security.builtin" : "card.security.none"]),
                new(Loc.Instance["card.core"], ServerFacts.CoreName(_server.Protocol)),
            };
            return details;
        }
    }

    // ── Действия ─────────────────────────────────────────────────────────────

    /// <summary>«Подключиться»: выбрать этот сервер и включить защиту через него.</summary>
    [RelayCommand]
    private async Task ConnectAsync()
    {
        _overlay.CloseSheet();
        await _controller.ConnectToAsync(Id).ConfigureAwait(true);
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveNameCommand))]
    public partial string NewName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsDetails))]
    public partial bool IsRenaming { get; private set; }

    [RelayCommand]
    private void StartRename()
    {
        NewName = Name;
        HideShare();
        IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    [RelayCommand(CanExecute = nameof(CanSaveName))]
    private void SaveName()
    {
        _catalog.Rename(Id, NewName);
        IsRenaming = false;
    }

    private bool CanSaveName() => NewName.Trim().Length is > 0 and <= MaxNameLength;

    /// <summary>Поделиться можно своим сервером — у демо-серверов ключа нет.</summary>
    public bool CanShare => _store.Contains(Id);

    /// <summary>Почему «Поделиться» недоступно; у доступного подсказки нет.</summary>
    public string? ShareHint => CanShare ? null : Loc.Instance["card.share.unavailable"];

    /// <summary>Открыт QR-код: вместо сведений — код, предупреждение и «Копировать ссылку».</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShareLabel), nameof(ShowsDetails))]
    public partial bool IsSharing { get; private set; }

    /// <summary>Сведения о подключении — когда не открыты ни переименование, ни QR-код.</summary>
    public bool ShowsDetails => !IsRenaming && !IsSharing;

    public string ShareLabel => Loc.Instance[IsSharing ? "card.share.hide" : "card.share"];

    /// <summary>Модули QR-кода ссылки; null — код закрыт.</summary>
    [ObservableProperty]
    public partial bool[,]? QrModules { get; private set; }

    [RelayCommand(CanExecute = nameof(CanShare))]
    private void ToggleShare()
    {
        if (IsSharing)
        {
            HideShare();
            return;
        }

        // Ключ читается из хранилища ОС только сейчас, по действию пользователя.
        if (_store.Definition(Id) is not { } definition || definition.Credentials == new ServerCredentials())
        {
            _toast.Show(Loc.Instance["card.share.noKey"]);
            return;
        }

        IsRenaming = false;
        _link = LinkWriter.Write(definition);
        QrModules = QrCode.Encode(_link);
        IsSharing = true;
    }

    [RelayCommand]
    private async Task CopyLinkAsync()
    {
        if (_link is { } link)
        {
            await _clipboard.SetTextAsync(link).ConfigureAwait(true);
            _toast.Show(Loc.Instance["card.share.copied"]);
        }
    }

    private void HideShare()
    {
        _link = null;
        QrModules = null;
        IsSharing = false;
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        var dialog = new DialogViewModel(
            DialogKind.Danger,
            "Icon.Trash",
            null,
            Loc.Instance.Format("dialog.deleteServer.title", Name),
            Loc.Instance[IsInUse ? "dialog.deleteServer.textInUse" : "dialog.deleteServer.text"],
            Loc.Instance["dialog.deleteServer.delete"],
            Loc.Instance["dialog.cancel"]);
        if (await _overlay.AskAsync(dialog).ConfigureAwait(true) != DialogChoice.Primary)
        {
            return;
        }

        _overlay.CloseSheet();
        await _controller.RemoveServerAsync(Id).ConfigureAwait(true);
        _toast.Show(Loc.Instance["card.deleted"]);
    }

    [RelayCommand]
    private void Close() => _overlay.CloseSheet();

    public void Dispose()
    {
        HideShare();
        _catalog.Changed -= OnCatalogChanged;
        _protection.PropertyChanged -= OnProtectionChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    private static string FormatAddress(string host, int port) =>
        (host.Contains(':', StringComparison.Ordinal) ? "[" + host + "]" : host) + ":" + port.ToString(CultureInfo.InvariantCulture);

    private void OnCatalogChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (_catalog.Servers.FirstOrDefault(s => s.Id == _server.Id) is { } fresh)
        {
            _server = fresh;
            OnPropertyChanged(string.Empty);
        }
    });

    private void OnProtectionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        OnPropertyChanged(string.Empty);

    private void OnLanguageChanged(object? sender, EventArgs e) => OnPropertyChanged(string.Empty);
}

/// <summary>Строка сведений: подпись слева, значение моноширинным справа.</summary>
public sealed record CardDetail(string Label, string Value);
