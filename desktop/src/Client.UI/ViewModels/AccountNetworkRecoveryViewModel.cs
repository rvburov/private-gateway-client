using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Account;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.UI.Localization;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>Одна явная операция восстановления для экрана входа и настроек; установка не запускается автоматически.</summary>
public sealed partial class AccountNetworkRecoveryViewModel : ObservableObject, IDisposable
{
    private readonly AccountService _account;
    private readonly IServiceInstaller _installer;
    private readonly IServiceAccess _access;

    public AccountNetworkRecoveryViewModel(AccountService account, IServiceInstaller installer, IServiceAccess access)
    {
        _account = account;
        _installer = installer;
        _access = access;
        account.Changed += OnAccountChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepairText))]
    [NotifyCanExecuteChangedFor(nameof(RepairCommand), nameof(RestoreCommand), nameof(OpenServiceSettingsCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial string? Error { get; private set; }

    public bool CanInstall => _installer.IsAvailable;
    public bool HasServiceSettings => _access.IsSupported;
    public string RepairText => Loc.Instance[IsBusy ? "welcome.service.busy" : "account.network.service"];
    private bool CanWork() => !IsBusy && !_account.IsRestoringNetwork;
    private bool CanRepair() => CanWork() && CanInstall;

    [RelayCommand(CanExecute = nameof(CanWork))]
    private async Task RestoreAsync()
    {
        Error = null;
        await _account.RestoreNetworkAsync().ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRepair))]
    private async Task RepairAsync()
    {
        Error = null;
        IsBusy = true;
        try
        {
            if (_access.IsSupported && await Task.Run(_access.Read).ConfigureAwait(true) == ServiceAccess.RequiresApproval)
            {
                Error = Loc.Instance["problem.background.text"];
                return;
            }

            var result = await _installer.InstallAsync(Loc.Instance["service.permission.update"], new Progress<int>()).ConfigureAwait(true);
            if (result == ServiceInstallResult.Installed)
            {
                await _account.RestoreNetworkAsync().ConfigureAwait(true);
            }
            else
            {
                Error = result == ServiceInstallResult.Cancelled ? Loc.Instance["problem.install.cancelled"]
                    : WelcomePageViewModel.InstallFailure(_installer);
            }
        }
        catch (Exception)
        {
            Error = Loc.Instance["problem.install.failed"];
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanWork))]
    private void OpenServiceSettings()
    {
        if (!_access.OpenSettings()) Error = Loc.Instance["problem.background.open_failed"];
    }

    private void OnAccountChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        RepairCommand.NotifyCanExecuteChanged();
        RestoreCommand.NotifyCanExecuteChanged();
        OpenServiceSettingsCommand.NotifyCanExecuteChanged();
    });

    private void OnLanguageChanged(object? sender, EventArgs e) => OnPropertyChanged(nameof(RepairText));

    public void Dispose()
    {
        _account.Changed -= OnAccountChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }
}
