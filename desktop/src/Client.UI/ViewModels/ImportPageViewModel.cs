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
    Qr,
    Link,
    File,
}

/// <summary>
/// «Добавить профиль» по макету (доска 3): источник — буфер обмена, QR-код (снимок области экрана в
/// буфере или картинка), ссылка или адрес подписки, файл; предпросмотр — сколько найдено, новых, дублей и ошибок с номерами строк; название
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
    [NotifyPropertyChangedFor(nameof(IsClipboard), nameof(IsQr), nameof(IsLink), nameof(IsFile))]
    public partial ImportSource Source { get; private set; }

    public bool IsClipboard => Source == ImportSource.Clipboard;

    public bool IsQr => Source == ImportSource.Qr;

    /// <summary>Подсказка карточки QR: как снять область экрана в буфер на этой ОС.</summary>
    public string QrText => Loc.Instance[OperatingSystem.IsWindows() ? "import.qr.text.windows"
        : OperatingSystem.IsMacOS() ? "import.qr.text.macos" : "import.qr.text.linux"];

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

    /// <summary>«base64 · VLESS / Hy2 / TUIC», «Clash · VLESS / Trojan».</summary>
    public string FormatText => Preview is not { } p
        ? string.Empty
        : string.Join(" · ", new[]
        {
            Loc.Instance["import.format." + p.Batch.Format],
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

    /// <summary>Новых нет, но у сохраненных обновится защита (решение 484) — кнопка так и называется.</summary>
    public string AddText => Preview switch
    {
        { New: > 0 } p => Loc.Instance.Plural("import.add", p.New),
        { Renewals: > 0 } p => Loc.Instance.Plural("import.renew", p.Renewals),
        _ => Loc.Instance["import.nothing"],
    };

    public bool CanAdd => (Preview is { New: > 0 } or { Renewals: > 0 }) && _store.CanStoreSecrets && !IsBusy;

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

    /// <summary>
    /// QR-код: снимок области экрана в буфере обмена (средствами ОС), а если в буфере не картинка —
    /// картинка из файла.
    /// </summary>
    [RelayCommand]
    private async Task FromQrAsync()
    {
        Source = ImportSource.Qr;
        if (await _clipboard.GetImageAsync().ConfigureAwait(true) is { } snapshot)
        {
            await FromImageAsync(snapshot).ConfigureAwait(true);
            return;
        }

        try
        {
            if (await _files.OpenImageAsync().ConfigureAwait(true) is { } file)
            {
                await FromImageAsync(file.Image).ConfigureAwait(true);
            }
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

    /// <summary>Картинка с QR-кодом — из буфера, файла или перетащенная в окно.</summary>
    public async Task FromImageAsync(ImagePixels image)
    {
        Source = ImportSource.Qr;
        var attempt = _attempt;
        // Распознавание большой картинки — заметная работа: не в потоке интерфейса.
        var text = await Task.Run(() => QrCode.Decode(image)).ConfigureAwait(true);
        if (attempt != _attempt)
        {
            // Пока распознавали, выбран другой источник или экран закрыт.
            return;
        }

        if (text is null)
        {
            Show(Loc.Instance["import.qr.none"]);
            return;
        }

        await AnalyzeAsync(text, suggestedName: null).ConfigureAwait(true);
    }

    /// <summary>Перетащенный файл не прочитан: большой, не текст или не картинка.</summary>
    public void ShowFileError(string reason) => Show(Loc.Instance.Format("import.file.failed", reason));

    /// <summary>Перетащенный в окно текстовый файл — как выбранный кнопкой «Открыть файл».</summary>
    public Task FromDroppedFileAsync(string name, string text)
    {
        Source = ImportSource.File;
        return AnalyzeAsync(text, Path.GetFileNameWithoutExtension(name));
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

        _toast.Show(outcome is { Added: 0, Renewed: > 0 }
            ? Loc.Instance.Plural("import.renewed", outcome.Renewed)
            : Loc.Instance.Plural("import.added", outcome.Added));
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
            SubscriptionContent content;
            try
            {
                content = await _downloader.DownloadAsync(url).ConfigureAwait(true);
            }
            catch (SubscriptionDownloadException e)
            {
                if (attempt == _attempt)
                {
                    Show(Loc.Instance.Format("import.download.failed", FailureText.Of(e.Failure, e.StatusCode)));
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
            trimmed = content.Text;
            // Название из Profile-Title (у PRIVATE GATEWAY — имя устройства), без него — домен подписки.
            suggestedName = content.Title ?? url.Host;
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
