namespace PrivateGateway.Client.UI.Services;

/// <summary>Страницы окна. Имена совпадают с ключами навигации островка.</summary>
public static class Pages
{
    public const string Home = "home";
    public const string Servers = "servers";
    public const string Check = "check";
    public const string Settings = "settings";
    public const string Add = "add";
}

/// <summary>Переход между страницами окна.</summary>
public interface INavigator
{
    void Navigate(string page);
}

/// <summary>Буфер обмена — через окно Avalonia; в тестах подменяется.</summary>
public interface IClipboardService
{
    Task SetTextAsync(string text);
}

/// <summary>Сохранение файла через окно выбора ОС; в тестах подменяется.</summary>
public interface IFileSaveService
{
    /// <summary>Спросить, куда сохранить, и записать текст в UTF-8.</summary>
    /// <returns>false — пользователь отменил сохранение.</returns>
    Task<bool> SaveTextAsync(string suggestedName, string text);
}
