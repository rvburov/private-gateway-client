using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Core.Updates;

namespace PrivateGateway.Client.Application.Updates;

/// <summary>Открыть окно заново, когда установщик обновления закончит работу (окно к тому времени закрыто).</summary>
public interface IAppRelauncher
{
    /// <param name="installerPid">Номер процесса установщика ОС, который запустила служба.</param>
    void RelaunchAfter(int installerPid);
}

/// <summary>Что с найденным обновлением в окне: «Обновить сейчас» → загрузка → готово → «Перезапустить клиент».</summary>
public enum UpdateStep
{
    /// <summary>Не начинали.</summary>
    None,

    Downloading,

    /// <summary>Скачано и сверено службой с подписанным манифестом.</summary>
    Ready,

    /// <summary>Установщик ОС запущен, окно закрывается и откроется уже новым.</summary>
    Installing,

    DownloadFailed,

    InstallFailed,
}

/// <summary>
/// Обновление программы для окна. Ищет его служба (<see cref="IGatewayClient.CheckForUpdateAsync"/>), а здесь — когда
/// спросить: через две минуты после запуска и потом раз в 12 часов, если включено «Проверять сами»
/// (<see cref="AppSettings.CheckUpdates"/>); при смене канала — сразу; кнопка «Проверить» — сразу и мимо памяти службы.
/// Выключено — окно само в сеть за обновлениями не ходит, только по кнопке. Последний ответ — <see cref="Offer"/>.
/// Найденное служба ставит сама, если может (<see cref="UpdateOffer.Installable"/>): <see cref="DownloadAsync"/> —
/// загрузка с ходом (<see cref="Step"/>, <see cref="Received"/> из <see cref="Total"/>), <see cref="InstallAsync"/> —
/// установщик ОС, перезапуск окна после него (<see cref="IAppRelauncher"/>) и <see cref="RestartRequested"/>: окно закрывается.
/// Откат на прежнюю версию (<see cref="UpdateOffer.Rollback"/>) — тем же путем: <see cref="RollBackAsync"/>, потом
/// <see cref="InstallAsync"/>; что качается и ставится — <see cref="Target"/>.
/// </summary>
public sealed class UpdateWatcher : IDisposable
{
    /// <summary>Первая проверка — не сразу: старт окна и подключение важнее.</summary>
    public static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(2);

    /// <summary>Как часто проверять самим.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromHours(12);

    // Как часто смотреть, не пора ли: после сна машины пропущенная проверка не ждет еще 12 часов.
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(10);

    private readonly IGatewayClient _gateway;
    private readonly SettingsService _settings;
    private readonly TimeProvider _time;
    private readonly IAppRelauncher _relauncher;
    private SynchronizationContext? _context;
    private ITimer? _timer;
    private UpdateChannel _channel;

    // Версия, о которой окно уже знает (видели в «О программе» или сказали уведомлением): о ней второй раз не говорят.
    private string? _known;

    public UpdateWatcher(IGatewayClient gateway, SettingsService settings, TimeProvider time, IAppRelauncher relauncher)
    {
        _gateway = gateway;
        _settings = settings;
        _time = time;
        _relauncher = relauncher;
        _channel = settings.Current.UpdateChannel;
    }

    /// <summary>Последний ответ службы; null — еще не спрашивали или служба не ответила (<see cref="Attempted"/>).</summary>
    public UpdateOffer? Offer { get; private set; }

    /// <summary>Когда спрашивали последний раз; null — ни разу.</summary>
    public DateTimeOffset? Attempted { get; private set; }

    public bool IsChecking { get; private set; }

    /// <summary>Версия, отложенная кнопкой «Позже» (до следующей версии или запуска окна); null — не откладывали.</summary>
    public string? Postponed { get; private set; }

    /// <summary>«Позже» для версии; null — показать снова.</summary>
    public void Postpone(string? version)
    {
        Postponed = version;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public UpdateStep Step { get; private set; }

    /// <summary>Что качается и ставится: найденное обновление или прежняя версия (откат); null — ничего.</summary>
    public string? Target { get; private set; }

    /// <summary>Идет откат на прежнюю версию, а не обновление.</summary>
    public bool IsRollingBack => Target is not null && Target == Offer?.Rollback && Step is not UpdateStep.None;

    /// <summary>Сколько байт скачано из скольких (<see cref="Total"/> — 0, пока служба не начала).</summary>
    public long Received { get; private set; }

    public long Total { get; private set; }

    /// <summary>Началась или кончилась проверка, пришел ответ, сдвинулась загрузка. В контексте окна.</summary>
    public event EventHandler? Changed;

    /// <summary>Установщик запущен, перезапуск окна назначен — окну пора закрыться.</summary>
    public event EventHandler? RestartRequested;

    /// <summary>
    /// Проверка по расписанию или после смены канала нашла версию, о которой окно еще не знало, — уведомление «Доступно
    /// обновление» (доски 19 и 22). Проверка кнопкой и при открытии «О программе» показывает ее на карточке и молчит.
    /// </summary>
    public event EventHandler<UpdateOffer>? Found;

    /// <summary>Проверять по расписанию; работа — в контексте вызова (окно).</summary>
    public void Start()
    {
        if (_timer is not null)
        {
            return;
        }

        _context = SynchronizationContext.Current;
        _timer = _time.CreateTimer(_ => Post(due: true), null, FirstCheck, Tick);
        _settings.Changed += OnSettingsChanged;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _settings.Changed -= OnSettingsChanged;
    }

    /// <summary>Пора ли проверить самим: включено «Проверять сами» и с прошлой попытки прошло <see cref="Period"/>.</summary>
    public bool IsDue => _settings.Current.CheckUpdates && (Attempted is not { } last || _time.GetUtcNow() - last >= Period);

    /// <summary>Спросить службу сейчас; уже спрашиваем — ничего.</summary>
    public Task CheckAsync(bool force, CancellationToken cancellationToken = default) => CheckAsync(force, announce: false, cancellationToken);

    private async Task CheckAsync(bool force, bool announce, CancellationToken cancellationToken = default)
    {
        if (IsChecking)
        {
            return;
        }

        IsChecking = true;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            Attempted = _time.GetUtcNow();
            var offer = await _gateway.CheckForUpdateAsync(_settings.Current.UpdateChannel, force, cancellationToken).ConfigureAwait(true);
            if (Target is not null && Target != offer?.Version && Target != offer?.Rollback && Step is not (UpdateStep.Downloading or UpdateStep.Installing))
            {
                // Служба больше не предлагает то, что качали, — загрузка ни к чему.
                (Step, Received, Total, Target) = (UpdateStep.None, 0, 0, null);
            }

            Offer = offer;
        }
        finally
        {
            IsChecking = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        if (Offer is { State: UpdateState.Available, Version: { } found } && found != _known)
        {
            _known = found;
            if (announce && found != Postponed)
            {
                Found?.Invoke(this, Offer);
            }
        }
    }

    /// <summary>«Обновить сейчас»: служба скачивает найденное и сверяет с манифестом; ход — в <see cref="Received"/>.</summary>
    public Task DownloadAsync(CancellationToken cancellationToken = default) =>
        Offer is { State: UpdateState.Available, Installable: true, Version: { } version } ? FetchAsync(version, cancellationToken) : Task.CompletedTask;

    /// <summary>«Откатить»: служба скачивает прежнюю версию так же, как обновление; дальше — <see cref="InstallAsync"/>.</summary>
    public Task RollBackAsync(CancellationToken cancellationToken = default) =>
        Offer?.Rollback is { } version ? FetchAsync(version, cancellationToken) : Task.CompletedTask;

    private async Task FetchAsync(string version, CancellationToken cancellationToken)
    {
        if (Step is UpdateStep.Downloading or UpdateStep.Installing)
        {
            return;
        }

        (Step, Received, Total, Target) = (UpdateStep.Downloading, 0, 0, version);
        Changed?.Invoke(this, EventArgs.Empty);
        await foreach (var progress in _gateway.DownloadUpdateAsync(version, cancellationToken).ConfigureAwait(true))
        {
            (Received, Total) = (progress.Received, progress.Total);
            Step = progress.Ready ? UpdateStep.Ready : progress.Failed ? UpdateStep.DownloadFailed : UpdateStep.Downloading;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        // Поток кончился без итога — служба пропала посреди загрузки.
        if (Step == UpdateStep.Downloading)
        {
            Step = UpdateStep.DownloadFailed;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// «Перезапустить клиент»: служба запускает установщик ОС, окно назначает себе запуск после него и просит закрыться.
    /// </summary>
    public async Task InstallAsync(CancellationToken cancellationToken = default)
    {
        if (Step is not (UpdateStep.Ready or UpdateStep.InstallFailed) || Target is not { } version)
        {
            return;
        }

        Step = UpdateStep.Installing;
        Changed?.Invoke(this, EventArgs.Empty);
        if (await _gateway.InstallUpdateAsync(version, cancellationToken).ConfigureAwait(true) is not { } installer)
        {
            Step = UpdateStep.InstallFailed;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        _relauncher.RelaunchAfter(installer);
        RestartRequested?.Invoke(this, EventArgs.Empty);
    }

    // Новый канал — другой список выпусков: спросить сразу (служба помнит ответ по каналу, лишнего запроса к GitHub нет).
    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (settings.UpdateChannel != _channel)
        {
            _channel = settings.UpdateChannel;
            if (settings.CheckUpdates || Attempted is not null)
            {
                Post(due: false);
            }
        }
    }

    private void Post(bool due)
    {
        if (_context is { } context)
        {
            context.Post(_ => _ = RunAsync(due), null);
        }
        else
        {
            _ = RunAsync(due);
        }
    }

    private async Task RunAsync(bool due)
    {
        if (!due || IsDue)
        {
            await CheckAsync(force: false, announce: true).ConfigureAwait(true);
        }
    }
}
