using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// Черновик внешнего прокси. Только явное сохранение меняет действующую конфигурацию; учетные данные
/// читает для актуальной редакции формы и передает защищенному хранилищу, не включает их в тексты ошибок и отчеты.
/// </summary>
public sealed partial class ExternalProxyEditorViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settings;
    private readonly IExternalProxyStore _store;
    private readonly INavigator _navigator;
    private ExternalProxySettings _saved;
    private bool _saving;
    private bool _disposed;
    private int _loadVersion;
    private int _editVersion;
    private bool _missingCredentials;
    private bool _credentialsLoaded;
    private string? _errorKey;
    private string _savedUsername = string.Empty;
    private string _savedPassword = string.Empty;

    public ExternalProxyEditorViewModel(SettingsService settings, IExternalProxyStore store, INavigator navigator)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(store);
        _settings = settings;
        _store = store;
        _navigator = navigator;
        _saved = settings.Current.ExternalProxy;
        ProtocolOptions = [.. Enum.GetValues<ExternalProxyProtocol>().Select(value =>
            new ChoiceOptionViewModel(() => ProtocolLabel(value), () => Protocol == value, () => ChooseProtocol(value)))];
        AuthenticationOptions = [.. Enum.GetValues<ProxyAuthentication>().Select(value =>
            new ChoiceOptionViewModel(() => AuthenticationLabel(value), () => Authentication == value, () => ChooseAuthentication(value)))];
        Load();
        settings.Changed += OnSettingsChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    [ObservableProperty]
    public partial ExternalProxyProtocol Protocol { get; set; }

    [ObservableProperty]
    public partial string Host { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Port { get; set; } = string.Empty;

    [ObservableProperty]
    public partial ProxyAuthentication Authentication { get; set; }

    [ObservableProperty]
    public partial string Username { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsProtocolOpen { get; set; }

    [ObservableProperty]
    public partial bool IsAuthenticationOpen { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(SaveState), nameof(SaveText))]
    public partial bool IsBusy { get; private set; }

    public string SaveText => Loc.Instance[IsBusy ? "externalProxy.busy" : "externalProxy.save"];

    public IReadOnlyList<ChoiceOptionViewModel> ProtocolOptions { get; }

    public IReadOnlyList<ChoiceOptionViewModel> AuthenticationOptions { get; }

    /// <summary>Завершение чтения полей хранилища, не проверка сетевой готовности прокси.</summary>
    public Task CredentialsRead { get; private set; } = Task.CompletedTask;

    public bool RequiresCredentials => Authentication == ProxyAuthentication.UserPassword;

    public string ProtocolText => ProtocolLabel(Protocol);

    public string AuthenticationText => AuthenticationLabel(Authentication);

    public string TransportNote => Loc.Instance[Protocol == ExternalProxyProtocol.Socks5
        ? "externalProxy.transport.socks5" : "externalProxy.transport.http"];

    public string EncryptionNote => Loc.Instance[Protocol == ExternalProxyProtocol.Https
        ? "externalProxy.encryption.tls" : "externalProxy.encryption.plain"];

    public string? Error => _errorKey is { } key ? Loc.Instance[key] : null;

    public bool HasError => _errorKey is not null;

    public bool HasChanges => Protocol != _saved.Protocol || Host != _saved.Host
        || Port != _saved.Port.ToString(CultureInfo.InvariantCulture) || Authentication != _saved.Authentication
        || Username != _savedUsername || Password != _savedPassword;

    public string SaveState => Loc.Instance[IsBusy ? "externalProxy.state.busy" : _missingCredentials && RequiresCredentials
        ? "externalProxy.state.credentials" : HasChanges ? "externalProxy.state.draft"
        : _saved.IsConfigured ? "externalProxy.state.saved" : "externalProxy.state.empty"];

    [RelayCommand]
    private void ToggleProtocol() => IsProtocolOpen = !IsProtocolOpen;

    [RelayCommand]
    private void ToggleAuthentication() => IsAuthenticationOpen = !IsAuthenticationOpen;

    [RelayCommand]
    private void OpenLocalSettings() => _navigator.Navigate(Pages.Core);

    private bool CanSave() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        _errorKey = null;
        if (string.IsNullOrWhiteSpace(Host))
        {
            SetError("externalProxy.error.host");
            return;
        }

        if (!int.TryParse(Port, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            SetError("externalProxy.error.port");
            return;
        }

        var draft = new ExternalProxySettings(Protocol, Host, port, Authentication);
        if (draft.Normalize() is not { } normalized)
        {
            SetError("externalProxy.error.host");
            return;
        }

        if (RequiresCredentials && (string.IsNullOrEmpty(Username) || string.IsNullOrEmpty(Password)
            || !ForwardProxyRules.AcceptsCredentials(Protocol == ExternalProxyProtocol.Socks5 ? ProxyProtocol.Socks5 : ProxyProtocol.Http,
                new ServerCredentials(Password: Password, Username: Username))))
        {
            SetError("externalProxy.error.credentials");
            return;
        }

        var username = Username;
        var password = Password;
        var needsCredentials = RequiresCredentials;
        var version = _editVersion;
        try
        {
            _saving = true;
            IsBusy = true;
            var stored = await Task.Run(() =>
            {
                if (needsCredentials && !_store.CanStoreSecrets)
                {
                    return false;
                }

                _store.Save(normalized, username, password);
                return true;
            }).ConfigureAwait(true);
            if (_disposed)
            {
                return;
            }

            if (!stored)
            {
                SetError("externalProxy.error.secrets");
                return;
            }

            // Пока хранилище отвечало, другая страница могла восстановить иной профиль.
            // Его метаданные и секреты загружаются заново одной редакцией, а не соединяются с черновиком сохранения.
            var preserveDraft = _editVersion != version;
            Load(preserveDraft, preserveDraft ? version : null);
            await CredentialsRead.ConfigureAwait(true);
        }
        catch (InvalidOperationException)
        {
            SetError("externalProxy.error.save");
        }
        catch (ArgumentException)
        {
            SetError("externalProxy.error.credentials");
        }
        finally
        {
            _saving = false;
            if (!_disposed)
            {
                IsBusy = false;
            }
        }
    }

    private void Load(bool preserveDraft = false, int? draftVersion = null)
    {
        _saved = _settings.Current.ExternalProxy;
        if (!preserveDraft)
        {
            Protocol = _saved.Protocol;
            Host = _saved.Host;
            Port = _saved.Port.ToString(CultureInfo.InvariantCulture);
            Authentication = _saved.Authentication;
            Username = string.Empty;
            Password = string.Empty;
        }

        _savedUsername = string.Empty;
        _savedPassword = string.Empty;
        _missingCredentials = false;
        _credentialsLoaded = _saved.Authentication != ProxyAuthentication.UserPassword;
        _errorKey = null;
        RefreshChoices();
        OnPropertyChanged(string.Empty);
        _loadVersion++;
        IsBusy = false;
        CredentialsRead = Task.CompletedTask;
        if (!_credentialsLoaded && (_settings.Current.Mode == ConnectionMode.Proxy || preserveDraft))
        {
            BeginCredentialsRead(draftVersion);
        }
    }

    private void BeginCredentialsRead(int? draftVersion = null)
    {
        IsBusy = true;
        CredentialsRead = ReadCredentialsAsync(_saved, ++_loadVersion, draftVersion ?? _editVersion);
    }

    private async Task ReadCredentialsAsync(ExternalProxySettings configuration, int version, int editVersion)
    {
        ServerCredentials? credentials;
        try
        {
            credentials = await Task.Run(_store.ReadCredentials).ConfigureAwait(true);
        }
        catch (InvalidOperationException)
        {
            credentials = null;
        }

        if (_disposed || version != _loadVersion)
        {
            return;
        }

        if (_settings.Current.ExternalProxy != configuration)
        {
            var preserveDraft = _editVersion != editVersion;
            Load(preserveDraft, preserveDraft ? editVersion : null);
            await CredentialsRead.ConfigureAwait(true);
            return;
        }

        _savedUsername = credentials?.Username ?? string.Empty;
        _savedPassword = credentials?.Password ?? string.Empty;
        if (_editVersion == editVersion)
        {
            Username = _savedUsername;
            Password = _savedPassword;
        }

        _missingCredentials = credentials is null;
        _credentialsLoaded = true;
        IsBusy = false;
        OnPropertyChanged(string.Empty);
    }

    private void SetError(string key)
    {
        _errorKey = key;
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
    }

    private void ChooseProtocol(ExternalProxyProtocol value)
    {
        Protocol = value;
        IsProtocolOpen = false;
        RefreshChoices();
    }

    private void ChooseAuthentication(ProxyAuthentication value)
    {
        Authentication = value;
        IsAuthenticationOpen = false;
        RefreshChoices();
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!string.IsNullOrEmpty(e.PropertyName) && e.PropertyName is nameof(Protocol) or nameof(Host) or nameof(Port)
            or nameof(Authentication) or nameof(Username) or nameof(Password))
        {
            _editVersion++;
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(HasChanges)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(SaveState)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(RequiresCredentials)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ProtocolText)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(AuthenticationText)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(TransportNote)));
            base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(EncryptionNote)));
        }
    }

    private static string ProtocolLabel(ExternalProxyProtocol protocol) => protocol switch
    {
        ExternalProxyProtocol.Http => "HTTP",
        ExternalProxyProtocol.Https => "HTTPS",
        _ => "SOCKS5",
    };

    private static string AuthenticationLabel(ProxyAuthentication authentication) => Loc.Instance[
        authentication == ProxyAuthentication.UserPassword ? "externalProxy.auth.password" : "externalProxy.auth.none"];

    private void OnSettingsChanged(object? sender, AppSettings next) => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed && !_saving && _settings.Current.ExternalProxy == next.ExternalProxy && next.ExternalProxy != _saved && !HasChanges)
        {
            Load();
        }
        else if (!_disposed && !_saving && !IsBusy && !_credentialsLoaded && next.Mode == ConnectionMode.Proxy
            && _settings.Current.Mode == ConnectionMode.Proxy && RequiresCredentials && !HasChanges)
        {
            BeginCredentialsRead();
        }
    });

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshChoices();
        OnPropertyChanged(string.Empty);
    }

    private void RefreshChoices()
    {
        foreach (var option in ProtocolOptions.Concat(AuthenticationOptions))
        {
            option.Refresh();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _loadVersion++;
        _settings.Changed -= OnSettingsChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
        Username = string.Empty;
        Password = string.Empty;
        _savedUsername = string.Empty;
        _savedPassword = string.Empty;
        GC.SuppressFinalize(this);
    }
}
