namespace PrivateGateway.Client.UI.Services;

/// <summary>Страницы окна. Имена совпадают с ключами навигации островка.</summary>
public static class Pages
{
    public const string Home = "home";
    public const string Servers = "servers";
    public const string Check = "check";
    public const string Settings = "settings";
    public const string Add = "add";

    /// <summary>«Защита и утечки» — подстраница настроек (доска 5).</summary>
    public const string Protection = "protection";

    /// <summary>«Профили и подписки» — подстраница настроек (доска 12).</summary>
    public const string Profiles = "profiles";
}

/// <summary>Переход между страницами окна.</summary>
public interface INavigator
{
    void Navigate(string page);

    /// <summary>Назад, на страницу, с которой пришли (экраны без островка: «Добавить профиль»).</summary>
    void Back();
}

/// <summary>Буфер обмена — через окно Avalonia; в тестах подменяется.</summary>
public interface IClipboardService
{
    Task SetTextAsync(string text);

    /// <returns>Текст из буфера; null — в буфере не текст.</returns>
    Task<string?> GetTextAsync();
}

/// <summary>Открытие текстового файла через окно выбора ОС; в тестах подменяется.</summary>
public interface IFileOpenService
{
    /// <returns>Имя файла без пути и текст; null — пользователь закрыл окно.</returns>
    /// <exception cref="InvalidDataException">Файл больше <see cref="MaxBytes"/> или не UTF-8.</exception>
    Task<(string Name, string Text)?> OpenTextAsync();

    /// <summary>Больше этого — не список серверов.</summary>
    const int MaxBytes = 1024 * 1024;
}

/// <summary>Сохранение файла через окно выбора ОС; в тестах подменяется.</summary>
public interface IFileSaveService
{
    /// <summary>Спросить, куда сохранить, и записать текст в UTF-8.</summary>
    /// <returns>false — пользователь отменил сохранение.</returns>
    Task<bool> SaveTextAsync(string suggestedName, string text);
}
