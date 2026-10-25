using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Servers;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// Первый запуск по макету (доска 10): приветствие с выбором языка, потом — «Добавьте первый сервер»,
/// если своих серверов еще нет. Шаг «Установка службы» появится вместе со службой: пока подключение —
/// имитация, ставить нечего.
/// </summary>
public sealed partial class WelcomePageViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly IServerStore _servers;
    private readonly IGatewayClient _gateway;
    private readonly INavigator _navigator;

    public WelcomePageViewModel(SettingsService settings, IServerStore servers, IGatewayClient gateway, INavigator navigator)
    {
        _settings = settings;
        _servers = servers;
        _gateway = gateway;
        _navigator = navigator;
        Loc.Instance.LanguageChanged += (_, _) => OnPropertyChanged(string.Empty);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGreeting))]
    public partial bool IsAddServer { get; private set; }

    public bool IsGreeting => !IsAddServer;

    public bool IsRussian
    {
        get => _settings.Current.Language == "ru";
        set => SetLanguage(value, "ru");
    }

    public bool IsEnglish
    {
        get => _settings.Current.Language == "en";
        set => SetLanguage(value, "en");
    }

    /// <summary>«Весь трафик Windows через VPN» — с именем текущей ОС.</summary>
    public string AllTrafficTitle => Loc.Instance.Format("welcome.all.title", AppInfo.OsName);

    /// <summary>В демо-режиме серверы можно посмотреть на демо-данных.</summary>
    public bool CanTryDemo => _gateway.Service == ServiceKind.Simulated;

    /// <summary>
    /// «Начать»: первый запуск пройден. Своих серверов нет — предложить добавить первый, есть (например,
    /// после восстановления копии) — сразу на главную.
    /// </summary>
    [RelayCommand]
    private void Start()
    {
        _settings.Update(s => s with { OnboardingDone = true });
        if (_servers.Servers.Count > 0)
        {
            _navigator.Navigate(Pages.Home);
            return;
        }

        IsAddServer = true;
    }

    [RelayCommand]
    private void AddServer() => _navigator.Navigate(Pages.Add);

    [RelayCommand]
    private void TryDemo() => _navigator.Navigate(Pages.Home);

    private void SetLanguage(bool on, string language)
    {
        if (!on)
        {
            return;
        }

        _settings.Update(s => s with { Language = language });
        Loc.Instance.SetLanguage(language);
    }
}
