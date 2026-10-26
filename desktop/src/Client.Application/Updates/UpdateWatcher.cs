using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Core.Updates;

namespace PrivateGateway.Client.Application.Updates;

/// <summary>
/// Обновление программы для окна. Ищет его служба (<see cref="IGatewayClient.CheckForUpdateAsync"/>), а здесь — когда
/// спросить: через две минуты после запуска и потом раз в 12 часов, если включено «Проверять сами»
/// (<see cref="AppSettings.CheckUpdates"/>); при смене канала — сразу; кнопка «Проверить» — сразу и мимо памяти службы.
/// Выключено — окно само в сеть за обновлениями не ходит, только по кнопке. Последний ответ — <see cref="Offer"/>.
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
    private SynchronizationContext? _context;
    private ITimer? _timer;
    private UpdateChannel _channel;

    public UpdateWatcher(IGatewayClient gateway, SettingsService settings, TimeProvider time)
    {
        _gateway = gateway;
        _settings = settings;
        _time = time;
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

    /// <summary>Началась или кончилась проверка, пришел ответ. В контексте окна.</summary>
    public event EventHandler? Changed;

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
    public async Task CheckAsync(bool force, CancellationToken cancellationToken = default)
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
            Offer = await _gateway.CheckForUpdateAsync(_settings.Current.UpdateChannel, force, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            IsChecking = false;
            Changed?.Invoke(this, EventArgs.Empty);
        }
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
            await CheckAsync(force: false).ConfigureAwait(true);
        }
    }
}
