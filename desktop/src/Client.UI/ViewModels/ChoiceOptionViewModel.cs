using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>Вариант во всплывающем списке выбора (доски 5, 6, 12): подпись, отмечен ли, что делать.</summary>
public sealed partial class ChoiceOptionViewModel(Func<string> label, Func<bool> isCurrent, Action choose) : ObservableObject
{
    public string Label => label();

    public bool IsCurrent => isCurrent();

    /// <summary>Сменился язык или выбранный вариант — перечитать подпись и отметку.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    [RelayCommand]
    private void Choose() => choose();
}
