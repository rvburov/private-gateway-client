using CommunityToolkit.Mvvm.ComponentModel;
using PrivateGateway.Client.UI.Localization;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>Страница, которая появится на следующих шагах этапа 1: заголовок и пояснение.</summary>
public sealed partial class StubPageViewModel : ObservableObject
{
    private readonly string _titleKey;

    public StubPageViewModel(string titleKey)
    {
        _titleKey = titleKey;
        Loc.Instance.LanguageChanged += (_, _) => OnPropertyChanged(string.Empty);
    }

    public string Title => Loc.Instance[_titleKey];

    public string Text => Loc.Instance["page.stub.text"];
}
