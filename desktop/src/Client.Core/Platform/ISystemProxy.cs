namespace PrivateGateway.Client.Core.Platform;

/// <summary>
/// Прежние настройки системного прокси — чтобы вернуть их как были, в том числе после сбоя службы
/// (снимок хранится в файле). Значение null — параметра не было.
/// </summary>
public sealed record SystemProxySnapshot(IReadOnlyDictionary<string, string?> Values);

/// <summary>
/// Системный прокси ОС для режима прокси: браузеры и программы, которые берут прокси из настроек ОС,
/// идут через вход ядра на 127.0.0.1. Вызывает служба либо контроллер Infrastructure в контексте пользователя окна.
/// </summary>
public interface ISystemProxy
{
    /// <summary>Где меняются настройки — для журнала: «параметры Интернета Windows», «GNOME».</summary>
    string Name { get; }

    /// <summary>Снимок текущих настроек — до включения.</summary>
    SystemProxySnapshot Read();

    /// <summary>Включить прокси на 127.0.0.1:<paramref name="port"/>; локальные адреса — напрямую.</summary>
    void Enable(int port);

    /// <summary>Вернуть настройки из снимка.</summary>
    void Restore(SystemProxySnapshot previous);

    /// <summary>
    /// Снимок, который даст <see cref="Enable"/> с этим портом. Окно сохраняет его до записи: если оно аварийно завершится
    /// посреди включения, при следующем запуске оно узнает свои настройки и вернет прежние. null — заранее не предсказать
    /// (такой прокси окну не подходит).
    /// </summary>
    SystemProxySnapshot? Expected(int port) => null;
}
