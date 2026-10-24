using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>Откуда взят текст для импорта — подсвечивается выбранная карточка.</summary>
public enum ImportSource
{
    None,
    Clipboard,
    Link,
    File,
}

/// <summary>
/// «Добавить профиль» по макету (доска 3): источник — буфер обмена, ссылка или адрес подписки,
/// файл; предпросмотр — сколько найдено, новых, дублей и ошибок с номерами строк; название
/// профиля и «Добавить N серверов». Сохраняет только <see cref="IServerStore"/>, секреты — в
/// хранилище ОС.
/// </summary>
public sealed partial class ImportPageViewModel : ObservableObject
{
    /// <summary>Сколько строк с ошибками показывать по отдельности; остальные — числом.</summary>
    public const int ErrorLinesShown = 3;

    private readonly IServerStore _store;
    private readonly ISubscriptionDownloader _downloader;
    private readonly IClipboardService _clipboard;
    private readonly IFileOpenService _files;
    private readonly INavigator _navigator;
    private readonly ToastViewModel _toast;
    private Uri? _subscriptionUrl;

    // Номер разбора: ответ подписки, пришедший после нового источника или ухода с экрана, не нужен.
    private int _attempt;

    public ImportPageViewModel(
        IServerStore store,
        ISubscriptionDownloader downloader,
        IClipboardService clipboard,
        IFileOpenService files,
        INavigator navigator,
        ToastViewModel toast)
    {
        _store = store;
        _downloader = downloader;
        _clipboard = clipboard;
        _files = files;
        _navigator = navigator;
        _toast = toast;
        Loc.Instance.LanguageChanged += (_, _) => OnPropertyChanged(string.Empty);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsClipboard), nameof(IsLink), nameof(IsFile))]
    public partial ImportSource Source { get; private set; }

    public bool IsClipboard => Source == ImportSource.Clipboard;

    public bool IsLink => Source == ImportSource.Link;

    public bool IsFile => Source == ImportSource.File;

    [ObservableProperty]
    public partial string LinkText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProfileName { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>Что не получилось: пустой буфер, подписка не загрузилась, файл не прочитан.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    public partial string? Message { get; private set; }

    public bool HasMessage => Message is not null;

    /// <summary>Хранилища секретов ОС нет — сохранить нельзя, и об этом сказано заранее.</summary>
    public string? StoreWarning => _store.CanStoreSecrets ? null : Loc.Instance.Format("import.store.unavailable", _store.SecretStoreName);

    public bool HasStoreWarning => StoreWarning is not null;

    /// <summary>Подписка загружена по http:// — ключи шли открыто; сказать об этом в предпросмотре.</summary>
    [ObservableProperty]
    public partial bool IsInsecure { get; private set; }

    // ── Предпросмотр ─────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreview), nameof(FormatText), nameof(FoundText), nameof(NewText),
        nameof(DuplicatesText), nameof(FailedText), nameof(FailedLabel), nameof(ErrorLines), nameof(HasErrors),
        nameof(AddText), nameof(CanAdd))]
    [NotifyCanExecuteChangedFor(nameof(AddCommand))]
    public partial ImportPreview? Preview { get; private set; }

    public bool HasPreview => Preview is not null;

    /// <summary>«base64 · VLESS / Hy2 / TUIC».</summary>
    public string FormatText => Preview is not { } p
        ? string.Empty
        : string.Join(" · ", new[]
        {
            Loc.Instance[p.Batch.Format == ImportParser.FormatBase64 ? "import.format.base64" : "import.format.links"],
            string.Join(" / ", p.Protocols.Select(ShortName)),
        }.Where(s => s.Length > 0));

    public string FoundText => Number(Preview?.Found);

    public string NewText => Number(Preview?.New);

    public string DuplicatesText => Number(Preview?.Duplicates);

    public string FailedText => Number(Preview?.Failed);

    /// <summary>«ошибка», «ошибки», «ошибок» — подпись под числом.</summary>
    public string FailedLabel => Loc.Instance.Plural("import.errors", Preview?.Failed ?? 0);

    /// <summary>«Строка 7: параметры не распознаны — пропущено.» — первые строки с ошибками.</summary>
    public IReadOnlyList<string> ErrorLines
    {
        get
        {
            if (Preview is not { } p)
            {
                return [];
            }

            var lines = p.Errors.Take(ErrorLinesShown)
                .Select(l => Loc.Instance.Format("import.line", l.Number, Loc.Instance["import.error." + l.Result.Error]))
                .ToList();
            if (p.Errors.Count > ErrorLinesShown)
            {
                lines.Add(Loc.Instance.Format("import.more", p.Errors.Count - ErrorLinesShown));
            }

            return lines;
        }
    }

    public bool HasErrors => ErrorLines.Count > 0;

    public string AddText => Preview is { New: > 0 } p
        ? Loc.Instance.Plural("import.add", p.New)
        : Loc.Instance["import.nothing"];

    public bool CanAdd => Preview is { New: > 0 } && _store.CanStoreSecrets && !IsBusy;

    // ── Источники ────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task FromClipboardAsync()
    {
        Source = ImportSource.Clipboard;
        var text = await _clipboard.GetTextAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(text))
        {
            Show(Loc.Instance["import.empty.clipboard"]);
            return;
        }

        await AnalyzeAsync(text, suggestedName: null).ConfigureAwait(true);
    }

    [RelayCommand]
    private void OpenLink()
    {
        Source = ImportSource.Link;
        Message = null;
    }

    [RelayCommand]
    private Task CheckLinkAsync() => AnalyzeAsync(LinkText, suggestedName: null);

    [RelayCommand]
    private async Task FromFileAsync()
    {
        Source = ImportSource.File;
        try
        {
            if (await _files.OpenTextAsync().ConfigureAwait(true) is not { } file)
            {
                return;
            }

            await AnalyzeAsync(file.Text, Path.GetFileNameWithoutExtension(file.Name)).ConfigureAwait(true);
        }
        catch (InvalidDataException e)
        {
            Show(Loc.Instance.Format("import.file.failed", e.Message));
        }
        catch (IOException e)
        {
            Show(Loc.Instance.Format("import.file.failed", e.Message));
        }
    }

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        if (Preview is not { } preview)
        {
            return;
        }

        var name = ProfileName.Trim();
        ImportOutcome outcome;
        try
        {
            outcome = _store.Add(preview.Servers, name.Length > 0 ? name : null, name.Length > 0 ? _subscriptionUrl : null);
        }
        catch (InvalidOperationException)
        {
            Message = StoreWarning ?? Loc.Instance.Format("import.store.unavailable", _store.SecretStoreName);
            return;
        }

        _toast.Show(Loc.Instance.Plural("import.added", outcome.Added));
        Reset();
        _navigator.Navigate(Pages.Servers);
    }

    [RelayCommand]
    private void Back()
    {
        Reset();
        _navigator.Back();
    }

    /// <summary>
    /// Текст из источника: одна строка с адресом http(s) — подписка, ее сначала загрузить; иначе —
    /// ссылки или base64 как есть.
    /// </summary>
    private async Task AnalyzeAsync(string text, string? suggestedName)
    {
        var attempt = ++_attempt;
        IsBusy = false;
        Message = null;
        Preview = null;
        IsInsecure = false;
        _subscriptionUrl = null;
        var trimmed = text.Trim();
        if (!trimmed.Contains('\n', StringComparison.Ordinal)
            && Uri.TryCreate(trimmed, UriKind.Absolute, out var url)
            && url.Scheme is "https" or "http")
        {
            IsBusy = true;
            Message = Loc.Instance["import.downloading"];
            try
            {
                trimmed = await _downloader.DownloadAsync(url).ConfigureAwait(true);
            }
            catch (SubscriptionDownloadException e)
            {
                if (attempt == _attempt)
                {
                    Show(Loc.Instance.Format("import.download.failed", DownloadReason(e)));
                }

                return;
            }
            finally
            {
                if (attempt == _attempt)
                {
                    IsBusy = false;
                }
            }

            if (attempt != _attempt)
            {
                return;
            }

            _subscriptionUrl = url;
            IsInsecure = url.Scheme == "http";
            suggestedName = url.Host;
            Message = null;
        }

        var preview = ImportPreview.Of(trimmed, _store);
        if (preview.Found == 0)
        {
            Show(Loc.Instance["import.empty.text"]);
            return;
        }

        Preview = preview;
        ProfileName = suggestedName ?? string.Empty;
    }

    private void Show(string message)
    {
        Preview = null;
        Message = message;
    }

    private void Reset()
    {
        _attempt++;
        IsBusy = false;
        IsInsecure = false;
        Source = ImportSource.None;
        LinkText = string.Empty;
        ProfileName = string.Empty;
        Preview = null;
        Message = null;
        _subscriptionUrl = null;
    }

    private static string DownloadReason(SubscriptionDownloadException e) => e switch
    {
        { Failure: DownloadFailure.Status, StatusCode: 404 } => Loc.Instance["import.download.not_found"],
        { Failure: DownloadFailure.Status, StatusCode: 401 or 403 } => Loc.Instance.Format("import.download.forbidden", e.StatusCode),
        { Failure: DownloadFailure.Status } => Loc.Instance.Format("import.download.status", e.StatusCode),
        { Failure: DownloadFailure.TooLarge } => Loc.Instance["import.download.too_large"],
        { Failure: DownloadFailure.Timeout } => Loc.Instance["import.download.timeout"],
        { Failure: DownloadFailure.NameResolution } => Loc.Instance["import.download.dns"],
        { Failure: DownloadFailure.Connection } => Loc.Instance["import.download.connection"],
        { Failure: DownloadFailure.Tls } => Loc.Instance["import.download.tls"],
        _ => Loc.Instance["import.download.network"],
    };

    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "0";

    /// <summary>Короткие имена протоколов для строки формата, как в макете.</summary>
    private static string ShortName(ProxyProtocol protocol) => protocol switch
    {
        ProxyProtocol.Shadowsocks => "SS",
        ProxyProtocol.Hysteria2 => "Hy2",
        ProxyProtocol.WireGuard => "WG",
        _ => ServerFacts.ProtocolName(protocol),
    };
}
