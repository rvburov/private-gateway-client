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

    /// <summary>При возврате учитывать также временно выключенные сети, чтобы на них не остался собственный прокси.</summary>
    SystemProxySnapshot ReadForRestore() => Read();

    /// <summary>Включить прокси на 127.0.0.1:<paramref name="port"/>; локальные адреса — напрямую.</summary>
    void Enable(int port);

    /// <summary>Применить прокси к зафиксированным целям. macOS не затрагивает сети, появившиеся после снимка.</summary>
    void Enable(int port, SystemProxySnapshot expected) => Enable(port);

    /// <summary>Вернуть настройки из снимка.</summary>
    void Restore(SystemProxySnapshot previous);

    /// <summary>
    /// Проверить прочитанное состояние после возврата снимка. Обычно значения совпадают точно;
    /// macOS сохраняет адрес выключенного прокси, хотя он уже не влияет на соединения.
    /// </summary>
    bool MatchesRestored(SystemProxySnapshot previous, SystemProxySnapshot current) =>
        previous.Values.Count == current.Values.Count
        && previous.Values.All(value => current.Values.TryGetValue(value.Key, out var actual) && actual == value.Value);

    /// <summary>
    /// Снимок, который даст <see cref="Enable(int)"/> с этим портом. Окно сохраняет его до записи: если оно аварийно завершится
    /// посреди включения, при следующем запуске оно узнает свои настройки и вернет прежние. null — заранее не предсказать
    /// (такой прокси окну не подходит).
    /// </summary>
    SystemProxySnapshot? Expected(int port) => null;
}
