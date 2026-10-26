using PrivateGateway.Client.Application.Import;

namespace PrivateGateway.Client.UI.Services;

/// <summary>Страницы окна. Имена совпадают с ключами навигации островка.</summary>
public static class Pages
{
    public const string Home = "home";
    public const string Servers = "servers";
    public const string Check = "check";
    public const string Settings = "settings";
    public const string Add = "add";

    /// <summary>Первый запуск (доска 10) — без островка.</summary>
    public const string Welcome = "welcome";

    /// <summary>«Защита и утечки» — подстраница настроек (доска 5).</summary>
    public const string Protection = "protection";

    /// <summary>«Подключение и ядро» — подстраница настроек (доска 6).</summary>
    public const string Core = "core";

    /// <summary>«Маршрутизация» — подстраница настроек (доска 13).</summary>
    public const string Routing = "routing";

    /// <summary>«Профили и подписки» — подстраница настроек (доска 12).</summary>
    public const string Profiles = "profiles";

    /// <summary>«Резервная копия» — подстраница настроек (доска 14).</summary>
    public const string Backup = "backup";

    /// <summary>«О программе» — подстраница настроек (доска 15).</summary>
    public const string About = "about";

    /// <summary>«Сеть машины» — сетевой инспектор, подстраница «Проверки» (этап 7).</summary>
    public const string Network = "network";

    /// <summary>«Проверка узла» — пинг, путь, DNS, TCP и TLS, подстраница «Проверки» (этап 7).</summary>
    public const string Probe = "probe";

    /// <summary>«Соединения и трафик» — сокеты с процессами и скорость адаптеров, подстраница «Проверки» (этап 7).</summary>
    public const string Connections = "connections";

    /// <summary>«До и после VPN» — что видят сайты и ОС до защиты и сейчас, подстраница «Проверки» (этап 7).</summary>
    public const string Compare = "compare";

    /// <summary>«Путь до сайта» — сетевая лаборатория: карта пути с измеренными и условными участками (этап 8).</summary>
    public const string Path = "path";

    /// <summary>Ошибка службы (доска 16): «Служба не запущена», «Нужны права администратора».</summary>
    public const string Problem = "problem";
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

    /// <returns>Картинка из буфера (снимок области экрана); null — в буфере не картинка.</returns>
    Task<ImagePixels?> GetImageAsync();
}

/// <summary>Выбор файла через окно ОС: текст со ссылками или программа; в тестах подменяется.</summary>
public interface IFileOpenService
{
    /// <returns>Имя файла без пути и текст; null — пользователь закрыл окно.</returns>
    /// <exception cref="InvalidDataException">Файл больше <see cref="MaxBytes"/> или не UTF-8.</exception>
    Task<(string Name, string Text)?> OpenTextAsync();

    /// <summary>Картинка с QR-кодом: PNG, JPEG, BMP и другие, что читает Avalonia.</summary>
    /// <returns>Имя файла без пути и пиксели; null — пользователь закрыл окно.</returns>
    /// <exception cref="InvalidDataException">Файл больше <see cref="MaxImageBytes"/> или не картинка.</exception>
    Task<(string Name, ImagePixels Image)?> OpenImageAsync();

    /// <summary>Программа для «мимо VPN»: исполняемый файл, на macOS — приложение .app.</summary>
    /// <returns>Полный путь и имя для подписи; null — пользователь закрыл окно.</returns>
    Task<(string Path, string Name)?> PickProgramAsync();

    /// <summary>Больше этого — не список серверов.</summary>
    const int MaxBytes = 1024 * 1024;

    /// <summary>Больше этого — не снимок экрана с QR-кодом.</summary>
    const int MaxImageBytes = 30 * 1024 * 1024;
}

/// <summary>Сохранение файла через окно выбора ОС; в тестах подменяется.</summary>
public interface IFileSaveService
{
    /// <summary>Спросить, куда сохранить, и записать текст в UTF-8.</summary>
    /// <returns>false — пользователь отменил сохранение.</returns>
    Task<bool> SaveTextAsync(string suggestedName, string text);
}
