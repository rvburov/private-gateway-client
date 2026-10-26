using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Core.Updates;

namespace PrivateGateway.Client.Application.Connection;

/// <summary>
/// Связь интерфейса со службой. Интерфейс сам сеть не трогает: подключение, проверки и пинг —
/// просьбы к службе, а состояние приходит от нее же (<see cref="StatusChanged"/>).
/// </summary>
public interface IGatewayClient
{
    /// <summary>Последнее состояние, присланное службой.</summary>
    ConnectionStatus Status { get; }

    /// <summary>Новое состояние от службы. Может прийти из любого потока.</summary>
    event EventHandler<ConnectionStatus>? StatusChanged;

    /// <summary>Какая служба отвечает окну: установленная, пользователя, никакой или имитация.</summary>
    ServiceKind Service { get; }

    Task ConnectAsync(ServerProfile server, CancellationToken cancellationToken = default);

    /// <summary>Что служба говорит о себе: версия, контракт, ядра; службы нет или она не ответила — null.</summary>
    Task<ServiceDetails?> GetDetailsAsync(CancellationToken cancellationToken = default) => Task.FromResult<ServiceDetails?>(null);

    /// <summary>
    /// Есть ли обновление программы в канале <paramref name="channel"/> — ищет служба (выпуски на GitHub, подпись,
    /// раскатка); <paramref name="force"/> — мимо ее памяти. Службы нет, она старая или не ответила — null.
    /// </summary>
    Task<UpdateOffer?> CheckForUpdateAsync(UpdateChannel channel, bool force, CancellationToken cancellationToken = default) =>
        Task.FromResult<UpdateOffer?>(null);

    /// <summary>
    /// Скачать обновление <paramref name="version"/>, найденное службой: ход загрузки, последнее — готово (файл сверен с
    /// подписанным манифестом) или отказ. Службы нет или она старая — пусто.
    /// </summary>
    IAsyncEnumerable<UpdateProgress> DownloadUpdateAsync(string version, CancellationToken cancellationToken = default) =>
        AsyncEnumerable.Empty<UpdateProgress>();

    /// <summary>Поставить скачанное обновление: служба запускает установщик ОС; ответ — номер его процесса, не вышло — null.</summary>
    Task<int?> InstallUpdateAsync(string version, CancellationToken cancellationToken = default) => Task.FromResult<int?>(null);

    /// <summary>
    /// «Продолжить (не рекомендуется)» на «Работает другой VPN»: подключиться, хотя служба нашла чужой
    /// туннель. Выбор действует до отключения — переподключения и смена настроек на ходу его не спрашивают
    /// снова. Где чужой VPN не проверяется (имитация), — обычное подключение.
    /// </summary>
    Task ConnectDespiteOtherVpnAsync(ServerProfile server, CancellationToken cancellationToken = default) =>
        ConnectAsync(server, cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Пауза для входа в Wi-Fi отеля или кафе: на <paramref name="duration"/> трафик идет мимо
    /// туннеля, чтобы открылась страница авторизации сети; потом защита возвращается сама. Только по
    /// явному действию пользователя. Защита выключена — паузе нечего снимать, ничего не происходит.
    /// </summary>
    /// <returns>Началась ли пауза: служба может ответить, что менять нечего (режим прокси без системного прокси).</returns>
    Task<bool> PauseForCaptivePortalAsync(TimeSpan duration, CancellationToken cancellationToken = default);

    /// <summary>Вернуть защиту раньше конца паузы.</summary>
    Task ResumeProtectionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Проверки защиты (служба, ядро, весь трафик, блок при обрыве, DNS, IPv6, внешний IP, другие
    /// VPN). Только читают состояние; заодно обновляют время проверки на главном экране.
    /// </summary>
    Task<DiagnosticReport> RunChecksAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Задержка через каждый сервер — меряет служба через ядро. В ответе только измеренные; службы
    /// нет (имитация, обрыв) — пусто, и прежние значения остаются.
    /// </summary>
    Task<IReadOnlyDictionary<string, LatencyResult>> MeasureLatencyAsync(IReadOnlyList<string> serverIds, CancellationToken cancellationToken = default);

    /// <summary>Последние записи журнала службы, от старых к новым; секреты уже скрыты.</summary>
    IReadOnlyList<JournalEntry> Journal { get; }

    /// <summary>Новая запись журнала службы. Может прийти из любого потока.</summary>
    event EventHandler<JournalEntry>? JournalAdded;
}
