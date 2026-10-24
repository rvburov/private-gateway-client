using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// Слой поверх окна вместе с островком: нижний лист (карточка сервера, доска 11) и диалог
/// подтверждения (доска 18). Один на окно; диалог открывается поверх листа.
/// </summary>
public sealed partial class OverlayViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSheet))]
    public partial object? Sheet { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog))]
    public partial DialogViewModel? Dialog { get; private set; }

    // Карточка подписана на каталог и состояние защиты — закрытый лист отписывается.
    partial void OnSheetChanged(object? oldValue, object? newValue) => (oldValue as IDisposable)?.Dispose();

    public bool HasSheet => Sheet is not null;

    public bool HasDialog => Dialog is not null;

    public void ShowSheet(object sheet) => Sheet = sheet;

    [RelayCommand]
    public void CloseSheet() => Sheet = null;

    /// <summary>Показать диалог и дождаться выбора. Escape и щелчок мимо — <see cref="DialogChoice.Dismissed"/>.</summary>
    public async Task<DialogChoice> AskAsync(DialogViewModel dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        Dialog?.Dismiss();
        Dialog = dialog;
        var choice = await dialog.Result.ConfigureAwait(true);
        if (Dialog == dialog)
        {
            Dialog = null;
        }

        return choice;
    }

    /// <summary>Escape или щелчок по затемнению: закрыть диалог безопасным вариантом, иначе лист.</summary>
    [RelayCommand]
    private void Dismiss()
    {
        if (Dialog is { } dialog)
        {
            dialog.Dismiss();
            return;
        }

        Sheet = null;
    }
}

/// <summary>Что выбрал пользователь в диалоге.</summary>
public enum DialogChoice
{
    Primary,
    Secondary,

    /// <summary>Закрыл диалог, ничего не выбрав, — значит, ничего не делать.</summary>
    Dismissed,
}

/// <summary>Вид диалога: цвет значка и кнопок (доска 18).</summary>
public enum DialogKind
{
    /// <summary>Предупреждение: оранжевый значок, основная кнопка — безопасный вариант.</summary>
    Warning,

    /// <summary>Вопрос: синий значок.</summary>
    Info,

    /// <summary>Удаление: красный значок и красная основная кнопка.</summary>
    Danger,
}

/// <summary>
/// Диалог подтверждения: значок, заголовок, текст и две кнопки во всю ширину. Опасный вариант не
/// выбирается ни Escape, ни щелчком мимо, и фокус при открытии — на безопасной кнопке.
/// </summary>
public sealed partial class DialogViewModel(
    DialogKind kind,
    string iconKey,
    string? detailKey,
    string title,
    string text,
    string primaryText,
    string secondaryText) : ObservableObject
{
    private readonly TaskCompletionSource<DialogChoice> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DialogKind Kind { get; } = kind;

    public string IconKey { get; } = iconKey;

    public string? DetailKey { get; } = detailKey;

    public string Title { get; } = title;

    public string Text { get; } = text;

    public string PrimaryText { get; } = primaryText;

    public string SecondaryText { get; } = secondaryText;

    public bool IsWarning => Kind == DialogKind.Warning;

    public bool IsDanger => Kind == DialogKind.Danger;

    /// <summary>
    /// Безопасная кнопка — основная («Оставить защиту»), кроме удаления: там основная — «Удалить»,
    /// а безопасная — «Отмена».
    /// </summary>
    public bool IsPrimarySafe => Kind != DialogKind.Danger;

    /// <summary>Вторая кнопка — опасное действие («Снять защиту»): красная рамка и текст.</summary>
    public bool IsSecondaryDanger { get; init; }

    public Task<DialogChoice> Result => _result.Task;

    [RelayCommand]
    private void Primary() => _result.TrySetResult(DialogChoice.Primary);

    [RelayCommand]
    private void Secondary() => _result.TrySetResult(DialogChoice.Secondary);

    internal void Dismiss() => _result.TrySetResult(DialogChoice.Dismissed);
}
