using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Account;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core.Account;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.UI.Localization;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// PIN-код блока сайтов (решение 742): спрашивает код перед тем, что ослабляет блок, и задает новый. Нет кода — разрешено
/// сразу. Пять неверных подряд — минута без попыток (подбор 4–8 цифр руками). Забыли — сброс входом в аккаунт сайта: блоки
/// остаются, код снимается.
/// </summary>
public sealed class BlockPinGate(SettingsService settings, OverlayViewModel overlay, AccountService account, TimeProvider time)
{
    /// <summary>Сколько неверных кодов подряд допускается до паузы.</summary>
    public const int Attempts = 5;

    /// <summary>Пауза после <see cref="Attempts"/> неверных кодов.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromMinutes(1);

    private int _failures;
    private DateTimeOffset _pausedUntil;

    public bool IsSet => settings.Current.BlockPin is not null;

    /// <summary>Сбросить код можно только входом в аккаунт: без аккаунта (демо) — нечем подтвердить.</summary>
    public bool CanReset => account.IsRequired;

    /// <summary>
    /// Спросить PIN-код листом с заголовком <paramref name="title"/> и текстом <paramref name="text"/>. true — код верный или
    /// его нет; false — «Оставить блок», закрыли лист или сбросили код через аккаунт (действие тогда не выполняется).
    /// </summary>
    public Task<bool> AllowAsync(string title, string text)
    {
        if (!IsSet)
        {
            return Task.FromResult(true);
        }

        var prompt = new BlockPinPromptViewModel(this, title, text);
        overlay.ShowSheet(prompt);
        return prompt.Result;
    }

    /// <summary>Лист «PIN-код для снятия блока»: задать новый; если код уже задан — сначала спросить его.</summary>
    public async Task ChangeAsync()
    {
        if (IsSet && !await AllowAsync(Loc.Instance["block.pin.change.title"], Loc.Instance["block.pin.change.text"]).ConfigureAwait(true))
        {
            return;
        }

        overlay.ShowSheet(new BlockPinSetupViewModel(this));
    }

    internal BlockPinCheck Check(string pin)
    {
        var now = time.GetUtcNow();
        if (now < _pausedUntil)
        {
            return BlockPinCheck.Paused;
        }

        if (settings.Current.BlockPin?.Matches(pin) ?? true)
        {
            _failures = 0;
            return BlockPinCheck.Right;
        }

        if (++_failures >= Attempts)
        {
            _failures = 0;
            _pausedUntil = now + Pause;
            return BlockPinCheck.Paused;
        }

        return BlockPinCheck.Wrong;
    }

    internal void Set(BlockPin? pin) => settings.Update(s => s with { BlockPin = pin });

    internal Task<LoginOutcome> SignInAsync(string email, string password, string? code) => account.SignInAsync(email, password, code, null);

    internal string? AccountEmail => account.Email;

    internal void ShowSheet(object sheet) => overlay.ShowSheet(sheet);

    internal void CloseSheet() => overlay.CloseSheet();
}

/// <summary>Ответ на введенный код.</summary>
internal enum BlockPinCheck
{
    Right,
    Wrong,
    Paused,
}

/// <summary>
/// Лист «Снять блок с …?» (доска 13, макет v2): текст, поле PIN-кода, «Забыли PIN-код?», «Оставить блок» и красная «Снять
/// блок». Закрытый лист — «Оставить блок».
/// </summary>
public sealed partial class BlockPinPromptViewModel : ObservableObject, IDisposable
{
    private readonly BlockPinGate _gate;
    private readonly TaskCompletionSource<bool> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal BlockPinPromptViewModel(BlockPinGate gate, string title, string text)
    {
        _gate = gate;
        Title = title;
        Text = text;
    }

    public string Title { get; }

    public string Text { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string Pin { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    public bool CanReset => _gate.CanReset;

    public Task<bool> Result => _result.Task;

    private bool CanConfirm => Pin.Length > 0;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        var check = _gate.Check(Pin);
        Pin = string.Empty;
        if (check == BlockPinCheck.Right)
        {
            _result.TrySetResult(true);
            _gate.CloseSheet();
            return;
        }

        Error = Loc.Instance[check == BlockPinCheck.Paused ? "block.pin.paused" : "block.pin.wrong"];
    }

    [RelayCommand]
    private void Keep()
    {
        _result.TrySetResult(false);
        _gate.CloseSheet();
    }

    /// <summary>«Забыли PIN-код?» — лист входа в аккаунт; действие, ради которого спрашивали, не выполняется.</summary>
    [RelayCommand]
    private void Forgot()
    {
        _result.TrySetResult(false);
        _gate.ShowSheet(new BlockPinResetViewModel(_gate));
    }

    /// <summary>Лист закрыли мимо кнопок — «Оставить блок».</summary>
    public void Dispose() => _result.TrySetResult(false);
}

/// <summary>
/// Лист «Сбросить PIN-код» (макет v2): вход аккаунтом Private Gateway — тем же, что в программе; администратору — второй
/// шаг с кодом 2FA. Вошли — код снят, блоки остались, и можно задать новый.
/// </summary>
public sealed partial class BlockPinResetViewModel : ObservableObject
{
    private readonly BlockPinGate _gate;

    internal BlockPinResetViewModel(BlockPinGate gate)
    {
        _gate = gate;
        Email = gate.AccountEmail ?? string.Empty;
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    public partial string Email { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    public partial string Password { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Code { get; set; } = string.Empty;

    /// <summary>Сайт ответил «нужен код»: видно поле кода 2FA.</summary>
    [ObservableProperty]
    public partial bool IsCodeStep { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ResetCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    private bool CanReset => !IsBusy && Email.Trim().Length > 0 && Password.Length > 0;

    [RelayCommand(CanExecute = nameof(CanReset))]
    private async Task ResetAsync()
    {
        // Сброс не меняет аккаунт программы: другая почта — отказ без запроса к сайту.
        if (_gate.AccountEmail is { } own && !string.Equals(own, Email.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            Error = LoginPageViewModel.ErrorText(AccountFailure.InvalidCredentials, 0);
            return;
        }

        IsBusy = true;
        Error = null;
        LoginOutcome outcome;
        try
        {
            outcome = await _gate.SignInAsync(Email.Trim(), Password, IsCodeStep ? Code.Trim() : null).ConfigureAwait(true);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or HttpRequestException)
        {
            outcome = LoginOutcome.Failed(AccountFailure.Network);
        }
        finally
        {
            IsBusy = false;
            Code = string.Empty;
        }

        if (outcome.Succeeded)
        {
            Password = string.Empty;
            _gate.Set(null);
            _gate.ShowSheet(new BlockPinSetupViewModel(_gate) { Notice = Loc.Instance["block.pin.reset.done"] });
            return;
        }

        if (outcome.Failure == AccountFailure.CodeRequired)
        {
            IsCodeStep = true;
            return;
        }

        Error = LoginPageViewModel.ErrorText(outcome.Failure, outcome.RetryAfterMinutes);
    }

    [RelayCommand]
    private void Cancel() => _gate.CloseSheet();
}

/// <summary>
/// Лист «PIN-код для снятия блока» (макет v2): что закрывает код, поле «PIN-код · 4–8 цифр», «Еще раз», «Задать PIN-код» и
/// «Отмена»; у заданного кода — еще «Убрать PIN-код».
/// </summary>
public sealed partial class BlockPinSetupViewModel : ObservableObject
{
    private readonly BlockPinGate _gate;

    internal BlockPinSetupViewModel(BlockPinGate gate)
    {
        _gate = gate;
        CanRemove = gate.IsSet;
    }

    /// <summary>Строка над формой: «PIN-код сброшен…» после входа в аккаунт.</summary>
    public string? Notice { get; init; }

    public bool HasNotice => Notice is not null;

    public bool CanRemove { get; }

    [ObservableProperty]
    public partial string Pin { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Repeat { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [RelayCommand]
    private void Save()
    {
        if (!BlockPin.IsValid(Pin))
        {
            Error = Loc.Instance["block.pin.invalid"];
            return;
        }

        if (Pin != Repeat)
        {
            Error = Loc.Instance["block.pin.mismatch"];
            return;
        }

        _gate.Set(BlockPin.Create(Pin));
        Pin = Repeat = string.Empty;
        _gate.CloseSheet();
    }

    [RelayCommand]
    private void Remove()
    {
        _gate.Set(null);
        _gate.CloseSheet();
    }

    [RelayCommand]
    private void Cancel() => _gate.CloseSheet();
}
