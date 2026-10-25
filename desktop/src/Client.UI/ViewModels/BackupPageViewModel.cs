using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Secrets;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Infrastructure.Backup;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// «Резервная копия» по макету (доска 14): файл .pgbackup под паролем — серверы и подписки,
/// настройки, по желанию ключи; восстановление из файла (выбор или перетаскивание) и из автокопии,
/// которую программа сама делает перед каждым восстановлением.
/// </summary>
public sealed partial class BackupPageViewModel : ObservableObject
{
    private readonly BackupService _backup;
    private readonly SettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly OverlayViewModel _overlay;
    private readonly ToastViewModel _toast;
    private readonly IFileSaveService _saver;
    private readonly IFileOpenService _opener;
    private readonly TimeProvider _time;
    private readonly INavigator _navigator;

    public BackupPageViewModel(
        BackupService backup,
        SettingsService settings,
        ISecretStore secrets,
        OverlayViewModel overlay,
        ToastViewModel toast,
        IFileSaveService saver,
        IFileOpenService opener,
        TimeProvider time,
        INavigator navigator)
    {
        _backup = backup;
        _settings = settings;
        _secrets = secrets;
        _overlay = overlay;
        _toast = toast;
        _saver = saver;
        _opener = opener;
        _time = time;
        _navigator = navigator;
        Loc.Instance.LanguageChanged += (_, _) => OnPropertyChanged(string.Empty);
    }

    // ── Сохранить копию ──────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanIncludeKeys))]
    public partial bool IncludeServers { get; set; } = true;

    [ObservableProperty]
    public partial bool IncludeSettings { get; set; } = true;

    /// <summary>Ключи — только вместе с серверами: без них ключам не к чему относиться.</summary>
    [ObservableProperty]
    public partial bool IncludeKeys { get; set; }

    public bool CanIncludeKeys => IncludeServers;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPasswordVisible { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveError))]
    public partial string? SaveError { get; private set; }

    public bool HasSaveError => SaveError is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsSaving { get; private set; }

    // ── Автокопия ────────────────────────────────────────────────────────────

    public bool HasAutoCopy => _backup.LastAutoCopy is not null;

    /// <summary>«сегодня в 09:12 · с ключами»: автокопия всегда полная.</summary>
    public string AutoCopyText
    {
        get
        {
            if (_backup.LastAutoCopy is not { } at)
            {
                return Loc.Instance["backup.auto.none"];
            }

            var day = TimeText.Day(at, _time);
            var today = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone).Date;
            var when = TimeZoneInfo.ConvertTime(at, _time.LocalTimeZone).Date == today ? Loc.Instance.Format("backup.auto.today", day) : day;
            return Loc.Instance.Format("backup.auto.meta", when);
        }
    }

    // ── Команды ──────────────────────────────────────────────────────────────

    partial void OnIncludeServersChanged(bool value)
    {
        if (!value)
        {
            IncludeKeys = false;
        }
    }

    [RelayCommand]
    private void Back() => _navigator.Navigate(Pages.Settings);

    [RelayCommand]
    private void TogglePassword() => IsPasswordVisible = !IsPasswordVisible;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!IncludeServers && !IncludeSettings)
        {
            SaveError = Loc.Instance["backup.save.nothing"];
            return;
        }

        if (Password.Length < BackupService.MinPasswordLength)
        {
            SaveError = Loc.Instance.Format("backup.password.short", BackupService.MinPasswordLength);
            return;
        }

        SaveError = null;
        IsSaving = true;
        try
        {
            var parts = new BackupParts(IncludeServers, IncludeSettings, IncludeServers && IncludeKeys);
            var password = Password;
            var text = await Task.Run(() => _backup.Create(parts, password)).ConfigureAwait(true);
            var day = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), _time.LocalTimeZone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (await _saver.SaveTextAsync($"private-gateway-{day}{BackupService.Extension}", text).ConfigureAwait(true))
            {
                Password = string.Empty;
                _toast.Show(Loc.Instance["backup.saved"]);
            }
        }
        finally
        {
            IsSaving = false;
        }
    }

    private bool CanSave() => !IsSaving;

    [RelayCommand]
    private async Task ChooseFileAsync()
    {
        try
        {
            if (await _opener.OpenTextAsync().ConfigureAwait(true) is { } file)
            {
                OpenFile(file.Name, file.Text);
            }
        }
        catch (InvalidDataException)
        {
            _toast.Show(Loc.Instance["backup.error.notBackup"]);
        }
    }

    /// <summary>Файл выбран или перетащен: не копия — сразу сказать, копия — спросить пароль.</summary>
    public void OpenFile(string name, string text)
    {
        if (!BackupService.IsBackup(text))
        {
            _toast.Show(Loc.Instance["backup.error.notBackup"]);
            return;
        }

        _overlay.ShowSheet(new RestoreBackupViewModel(name, text, this, _overlay, _time));
    }

    /// <summary>Перетащили не файл или файл не прочитать.</summary>
    public void ShowNotBackup() => _toast.Show(Loc.Instance["backup.error.notBackup"]);

    [RelayCommand]
    private async Task RestoreAutoCopyAsync()
    {
        if (_backup.LastAutoCopy is not { } at
            || await _overlay.AskAsync(new DialogViewModel(
                DialogKind.Info,
                "Icon.Adv.Backup",
                null,
                Loc.Instance["dialog.autocopy.title"],
                Loc.Instance.Format("dialog.autocopy.text", TimeText.Day(at, _time)),
                Loc.Instance["dialog.cancel"],
                Loc.Instance["dialog.autocopy.restore"])).ConfigureAwait(true) != DialogChoice.Secondary)
        {
            return;
        }

        try
        {
            Report(_backup.RestoreAutoCopy());
        }
        catch (Exception e) when (e is BackupException or InvalidOperationException)
        {
            _toast.Show(Loc.Instance["backup.error.autocopy"], ToastViewModel.LongDuration);
        }
    }

    /// <summary>Заменить серверы и настройки содержимым копии; перед этим служба сама делает автокопию.</summary>
    public void Apply(BackupContent content)
    {
        try
        {
            Report(_backup.Restore(content));
        }
        catch (InvalidOperationException)
        {
            _toast.Show(Loc.Instance.Format("backup.error.store", _secrets.Name), ToastViewModel.LongDuration);
        }
    }

    private void Report(RestoreSummary summary)
    {
        Loc.Instance.SetLanguage(_settings.Current.Language);
        var parts = new List<string>();
        if (summary.Servers is { } servers)
        {
            parts.Add(Loc.Instance.Plural("backup.count", servers.Restored));
        }

        if (summary.Settings)
        {
            parts.Add(Loc.Instance["backup.part.settings"]);
        }

        var text = Loc.Instance.Format("backup.restored", string.Join(", ", parts));
        if (summary.Servers is { WithoutKeys: > 0 } skipped)
        {
            text += " · " + Loc.Instance.Plural("backup.skipped", skipped.WithoutKeys);
        }

        _toast.Show(text, ToastViewModel.LongDuration);
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>
/// Лист «Восстановить из копии»: сначала пароль, потом что в копии и «Восстановить» — до этого
/// шага ничего не меняется.
/// </summary>
public sealed partial class RestoreBackupViewModel : ObservableObject
{
    private readonly string _text;
    private readonly BackupPageViewModel _page;
    private readonly OverlayViewModel _overlay;
    private readonly TimeProvider _time;

    public RestoreBackupViewModel(string name, string text, BackupPageViewModel page, OverlayViewModel overlay, TimeProvider time)
    {
        FileName = name;
        _text = text;
        _page = page;
        _overlay = overlay;
        _time = time;
    }

    public string FileName { get; }

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPasswordVisible { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial bool IsOpening { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpened), nameof(IsLocked), nameof(Summary))]
    public partial BackupContent? Content { get; private set; }

    public bool IsOpened => Content is not null;

    public bool IsLocked => Content is null;

    /// <summary>«Копия от 25 окт. 09:12: серверы и подписки (3, с ключами), настройки».</summary>
    public string Summary
    {
        get
        {
            if (Content is not { } content)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if (content.HasServers)
            {
                parts.Add(Loc.Instance.Format(content.HasKeys ? "backup.part.serversKeys" : "backup.part.serversNoKeys", content.ServerCount));
            }

            if (content.HasSettings)
            {
                parts.Add(Loc.Instance["backup.part.settings"]);
            }

            var created = TimeZoneInfo.ConvertTime(content.CreatedAt, _time.LocalTimeZone);
            return Loc.Instance.Format("backup.summary", created.ToString("d MMMM yyyy, HH:mm", Loc.Instance.Culture), string.Join(", ", parts));
        }
    }

    [RelayCommand]
    private void TogglePassword() => IsPasswordVisible = !IsPasswordVisible;

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenAsync()
    {
        Error = null;
        IsOpening = true;
        try
        {
            var password = Password;
            Content = await Task.Run(() => BackupService.Read(_text, password)).ConfigureAwait(true);
            Password = string.Empty;
        }
        catch (BackupException e)
        {
            Error = Loc.Instance[e.Error switch
            {
                BackupError.WrongPassword => "backup.error.password",
                BackupError.NewerVersion => "backup.error.newer",
                _ => "backup.error.notBackup",
            }];
        }
        finally
        {
            IsOpening = false;
        }
    }

    private bool CanOpen() => !IsOpening;

    [RelayCommand]
    private void Restore()
    {
        if (Content is { } content)
        {
            _overlay.CloseSheet();
            _page.Apply(content);
        }
    }

    [RelayCommand]
    private void Cancel() => _overlay.CloseSheet();
}
