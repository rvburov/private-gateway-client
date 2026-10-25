namespace PrivateGateway.Client.Core.Platform;

/// <summary>
/// Прежние настройки системного прокси — чтобы вернуть их как были, в том числе после сбоя службы
/// (снимок хранится в файле). Значение null — параметра не было.
/// </summary>
public sealed record SystemProxySnapshot(IReadOnlyDictionary<string, string?> Values);

/// <summary>
/// Системный прокси ОС для режима прокси: браузеры и программы, которые берут прокси из настроек ОС,
/// идут через вход ядра на 127.0.0.1. Реализации — в Client.Platform.*, вызывает только служба.
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
}
