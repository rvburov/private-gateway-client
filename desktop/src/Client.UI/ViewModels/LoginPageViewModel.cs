using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Account;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core.Account;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.Services;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// Вход в аккаунт сайта (доска 10, решение 621): до любого другого экрана, и до приветствия первого запуска; островка нет.
/// Сверху — язык RU/EN, как на приветствии: язык выбирают до него. Два шага (0.2.1, 2FA — только у администраторов):
/// сначала почта, пароль с «глазом» и «Войти»; если сайт ответил «нужен код» (<see cref="AccountFailure.CodeRequired"/>),
/// второй шаг — код 2FA или резервный код, «Войти» и «Войти другим аккаунтом» (<see cref="IsCodeStep"/>). Почта и пароль
/// первого шага на втором остаются только в памяти модели — для повторного входа с кодом. «Войти» — «Вход…» и недоступна,
/// пока идет запрос; Enter в поле отправляет форму. После ответа код стирается всегда, пароль — на первом шаге и когда
/// второй шаг кончился (вошли, неверный пароль, «Войти другим аккаунтом»); почта остается. Ошибка — строкой под формой;
/// у 403 и 409 — кнопка на страницу сайта (решение 626). Почему вход нужен снова (доступ отозван, срок истек) — плашкой
/// над формой. Уйти с экрана после входа решает окно (<see cref="ShellViewModel"/>): оно же держит ворота.
/// </summary>
public sealed partial class LoginPageViewModel : ObservableObject, IDisposable
{
    /// <summary>Код 2FA — ровно столько цифр.</summary>
    public const int CodeLength = 6;

    /// <summary>
    /// Резервный код — не длиннее, чем принимает сайт; столько же поле принимает и для кода 2FA, чтобы вставка «123 456»
    /// не обрезалась до разбора (лишнее отбрасывает модель).
    /// </summary>
    public const int RecoveryMaxLength = 64;

    /// <summary>Почта — не длиннее этого (как на Android).</summary>
    public const int EmailMaxLength = 320;

    /// <summary>Пароль — не длиннее этого (как на Android).</summary>
    public const int PasswordMaxLength = 4096;

    private readonly AccountService _account;
    private readonly SettingsService _settings;
    private readonly IUrlLauncher _launcher;

    public LoginPageViewModel(AccountService account, SettingsService settings, IUrlLauncher launcher)
    {
        _account = account;
        _settings = settings;
        _launcher = launcher;
        _account.Changed += OnAccountChanged;
        Loc.Instance.LanguageChanged += OnLanguageChanged;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text))]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string Email { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPasswordVisible { get; private set; }

    /// <summary>Код 2FA (только цифры, не больше <see cref="CodeLength"/>) или резервный код (<see cref="IsRecovery"/>).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial string Code { get; set; } = string.Empty;

    /// <summary>
    /// Второй шаг: сайт принял почту и пароль и ждет код администратора. Поля почты и пароля скрыты, видно поле кода.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Text))]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial bool IsCodeStep { get; private set; }

    /// <summary>Вход по резервному коду вместо кода 2FA.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CodeLabel), nameof(CodeHint), nameof(CodeToggleText))]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    public partial bool IsRecovery { get; private set; }

    /// <summary>Идет запрос: «Вход…», кнопка недоступна.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignInText))]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand), nameof(BackCommand))]
    public partial bool IsBusy { get; private set; }

    /// <summary>Почему последняя попытка не вышла; null — ошибки нет.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error), nameof(HasError), nameof(CanOpenSite), nameof(CanOpenDevices))]
    public partial AccountFailure? Failure { get; private set; }

    /// <summary>Для «Слишком много попыток» — сколько минут ждать.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Error))]
    public partial int RetryMinutes { get; private set; }

    /// <summary>«Вход в Private Gateway» или на втором шаге «Код подтверждения администратора».</summary>
    public string Title => Loc.Instance[IsCodeStep ? "account.code.title" : "account.title"];

    /// <summary>Пояснение под заголовком; на втором шаге — чей это аккаунт.</summary>
    public string Text => IsCodeStep ? Loc.Instance.Format("account.code.text", Email.Trim()) : Loc.Instance["account.text"];

    public string CodeLabel => Loc.Instance[IsRecovery ? "account.recovery" : "account.code"];

    public string CodeHint => Loc.Instance[IsRecovery ? "account.recovery.hint" : "account.code.hint"];

    /// <summary>«Войти по резервному коду» ↔ «Войти по коду 2FA».</summary>
    public string CodeToggleText => Loc.Instance[IsRecovery ? "account.code.use" : "account.recovery.use"];

    public string SignInText => Loc.Instance[IsBusy ? "account.signingIn" : "account.signIn"];

    /// <summary>Текст ошибки на языке окна: при смене языка пересчитывается.</summary>
    public string? Error => Failure switch
    {
        null => null,
        AccountFailure.InvalidCredentials => Loc.Instance["account.error.invalid"],
        AccountFailure.InvalidCode => Loc.Instance["account.error.code"],
        AccountFailure.SetupRequired => Loc.Instance["account.error.setupRequired"],
        AccountFailure.DeviceLimit => Loc.Instance["account.error.deviceLimit"],
        AccountFailure.DeviceChangeLimit => Loc.Instance["account.error.deviceChanges"],
        AccountFailure.RateLimited => Loc.Instance.Format("account.error.rateLimited", Math.Max(1, RetryMinutes)),
        AccountFailure.Validation => Loc.Instance["account.error.validation"],
        AccountFailure.Network => Loc.Instance["account.error.network"],
        AccountFailure.Store => Loc.Instance["account.error.store"],
        _ => Loc.Instance["account.error.server"],
    };

    public bool HasError => Failure is not null;

    /// <summary>403: 2FA настраивается на сайте — «Открыть сайт».</summary>
    public bool CanOpenSite => Failure == AccountFailure.SetupRequired;

    /// <summary>409: устройства — на странице устройств сайта, «Устройства на сайте».</summary>
    public bool CanOpenDevices => Failure is AccountFailure.DeviceLimit or AccountFailure.DeviceChangeLimit;

    /// <summary>Почему вход нужен снова: доступ отозван на сайте или срок истек; null — первый вход или выход.</summary>
    public string? Notice => _account.LastMessage switch
    {
        AccountMessage.Revoked => Loc.Instance["account.revoked"],
        AccountMessage.Expired => Loc.Instance["account.expired"],
        _ => null,
    };

    public bool HasNotice => Notice is not null;

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

    /// <summary>«Войти» и Enter: поля шага заполнены, запрос не идет. Первый шаг — без кода, второй — с кодом.</summary>
    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        var codeStep = IsCodeStep;
        var code = codeStep ? Code : null;
        Failure = null;
        RetryMinutes = 0;
        IsBusy = true;
        LoginOutcome outcome;
        try
        {
            outcome = await _account.SignInAsync(Email, Password, IsRecovery ? null : code, IsRecovery ? code : null).ConfigureAwait(true);
        }
        catch (Exception e)
        {
            // Исключение из команды уронило бы окно (глобального обработчика нет): любой сбой — строкой под формой.
            outcome = LoginOutcome.Failed(e is IOException or OperationCanceledException ? AccountFailure.Network : AccountFailure.Server);
        }
        finally
        {
            // Код не держим дольше запроса, почта остается для следующей попытки.
            IsBusy = false;
            Code = string.Empty;
        }

        RetryMinutes = outcome.RetryAfterMinutes;
        switch (outcome.Failure)
        {
            case AccountFailure.CodeRequired:
                // Пароль верный, аккаунт администратора: второй шаг. Почта и пароль остаются в памяти для входа с кодом.
                IsRecovery = false;
                IsCodeStep = true;
                break;
            case AccountFailure.InvalidCredentials when codeStep:
                // Пароль сменили, пока вводили код: снова первый шаг, с ошибкой.
                ToStepOne();
                Failure = outcome.Failure;
                break;
            case null:
                ToStepOne();
                break;
            default:
                // На первом шаге пароль не держим дольше запроса; на втором он нужен для следующего кода.
                if (!codeStep)
                {
                    Password = string.Empty;
                }

                Failure = outcome.Failure;
                break;
        }
    }

    private bool CanSignIn() => !IsBusy && !string.IsNullOrWhiteSpace(Email) && Password.Length > 0
        && (!IsCodeStep || (IsRecovery ? !string.IsNullOrWhiteSpace(Code) : Code.Length == CodeLength));

    /// <summary>«Войти другим аккаунтом» на втором шаге: снова почта и пароль; почта остается, пароль и код стерты.</summary>
    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        ToStepOne();
        Failure = null;
        RetryMinutes = 0;
    }

    private bool CanGoBack() => !IsBusy;

    [RelayCommand]
    private void TogglePassword() => IsPasswordVisible = !IsPasswordVisible;

    /// <summary>Поле кода переключается между кодом 2FA и резервным и очищается.</summary>
    [RelayCommand]
    private void ToggleRecovery()
    {
        IsRecovery = !IsRecovery;
        Code = string.Empty;
        Failure = null;
    }

    [RelayCommand]
    private async Task OpenSiteAsync() => await _launcher.OpenAsync(AccountSite.LoginPage).ConfigureAwait(true);

    [RelayCommand]
    private async Task OpenDevicesAsync() => await _launcher.OpenAsync(AccountSite.DevicesPage).ConfigureAwait(true);

    /// <summary>Пустая форма: после выхода и потери доступа экран входа открывается с чистыми полями, на первом шаге.</summary>
    public void Reset()
    {
        Email = string.Empty;
        ToStepOne();
        IsPasswordVisible = false;
        Failure = null;
        RetryMinutes = 0;
    }

    public void Dispose()
    {
        _account.Changed -= OnAccountChanged;
        Loc.Instance.LanguageChanged -= OnLanguageChanged;
    }

    // Код 2FA — только цифры и не больше шести: вставка «123 456» или с буквами не ломает вход.
    partial void OnCodeChanged(string value)
    {
        if (IsRecovery)
        {
            return;
        }

        var digits = new string([.. value.Where(char.IsAsciiDigit).Take(CodeLength)]);
        if (digits != value)
        {
            Code = digits;
        }
    }

    // Первый шаг: пароль и код стерты, поле кода — снова для кода 2FA.
    private void ToStepOne()
    {
        IsCodeStep = false;
        IsRecovery = false;
        Password = string.Empty;
        Code = string.Empty;
    }

    private void OnAccountChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        OnPropertyChanged(nameof(Notice));
        OnPropertyChanged(nameof(HasNotice));
    });

    private void OnLanguageChanged(object? sender, EventArgs e) => OnPropertyChanged(string.Empty);

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
