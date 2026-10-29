using System.Text;
using Microsoft.Extensions.Logging;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Import;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Application.Servers;

/// <summary>Почему обновление подписки сейчас не начинается.</summary>
public enum UpdateBlock
{
    None,

    /// <summary>«Обновлять только через VPN», а защита не включена.</summary>
    ProtectionOff,

    /// <summary>«Обновлять только через VPN», а службы VPN нет — работает имитация.</summary>
    ServiceMissing,

    /// <summary>Эта подписка уже обновляется.</summary>
    Busy,

    /// <summary>Подписка удалена или изменена, пока загружался ответ.</summary>
    Superseded,
}

/// <summary>Итог попытки: обновлено, не удалось (причина сохранена у подписки) или не начато.</summary>
public sealed record UpdateResult(SubscriptionUpdate? Update, DownloadFailure? Failure = null, int? StatusCode = null, UpdateBlock Block = UpdateBlock.None)
{
    public bool IsUpdated => Update is not null;
}

/// <summary>Выбранный сервер пропал из подписки или изменился после обновления — сказать пользователю.</summary>
public sealed record SelectedServerNotice(string SubscriptionName, string ServerName, SelectedServerChange Change);

/// <summary>Подписка не обновилась по расписанию — прошлый список оставлен; причина и код ответа сервера подписки.</summary>
public sealed record UpdateFailureNotice(string SubscriptionName, DownloadFailure Failure, int? StatusCode);

/// <summary>
/// Обновление подписок: по кнопке и по расписанию из настроек. Неудача не трогает прошлый список —
/// причина запоминается у подписки. «Только через VPN» — пока защита не включена через настоящую
/// службу, ничего не загружается. Выбранный сервер не подменяется никогда (<see cref="IServerStore.ApplyUpdate"/>).
/// </summary>
public sealed partial class SubscriptionUpdater : IDisposable
{
    /// <summary>Как часто смотреть, не пора ли обновить подписки по расписанию.</summary>
    public static readonly TimeSpan CheckPeriod = TimeSpan.FromMinutes(10);

    /// <summary>Первая проверка после запуска — не сразу, чтобы не мешать старту окна.</summary>
    public static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);

    /// <summary>После неудачи по расписанию — не чаще раза в час.</summary>
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);

    private readonly IServerStore _store;
    private readonly ISubscriptionDownloader _downloader;
    private readonly IGatewayClient _gateway;
    private readonly SettingsService _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<SubscriptionUpdater> _logger;
    private readonly HashSet<string> _busy = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (Uri Address, CancellationTokenSource Stop)> _operations = new(StringComparer.Ordinal);
    private readonly Lock _operationGate = new();
    private SynchronizationContext? _context;
    private ITimer? _timer;

    public SubscriptionUpdater(
        IServerStore store,
        ISubscriptionDownloader downloader,
        IGatewayClient gateway,
        SettingsService settings,
        TimeProvider time,
        ILogger<SubscriptionUpdater> logger)
    {
        _store = store;
        _downloader = downloader;
        _gateway = gateway;
        _settings = settings;
        _time = time;
        _logger = logger;
        _store.Changed += OnStoreChanged;
    }

    /// <summary>Началось или закончилось обновление какой-то подписки — для крутилки на кнопке.</summary>
    public event EventHandler? BusyChanged;

    /// <summary>Выбранный сервер пропал или изменился, а пользователь просил об этом сообщать.</summary>
    public event EventHandler<SelectedServerNotice>? SelectedServerChanged;

    /// <summary>Подписка не обновилась по расписанию. По кнопке «Обновить» причину показывает сам экран подписок.</summary>
    public event EventHandler<UpdateFailureNotice>? ScheduledUpdateFailed;

    /// <summary>Можно ли обновлять сейчас — по настройке «только через VPN» и состоянию защиты.</summary>
    public UpdateBlock Block =>
        !_settings.Current.UpdateOnlyViaVpn ? UpdateBlock.None
        : _gateway.Service is ServiceKind.Simulated or ServiceKind.None ? UpdateBlock.ServiceMissing
        : _gateway.Status is not { State: ConnectionState.Connected, Mode: ConnectionMode.Vpn, IsPaused: false } ? UpdateBlock.ProtectionOff
        : UpdateBlock.None;

    public bool IsUpdating(string subscriptionId)
    {
        lock (_operationGate)
        {
            return _busy.Contains(subscriptionId);
        }
    }

    /// <summary>
    /// Обновлять по расписанию. Работа идет в контексте вызова (окно), загрузка — в фоне. Еще одна
    /// попытка — сразу после включения защиты и после смены настроек.
    /// </summary>
    public void Start()
    {
        if (_timer is not null)
        {
            return;
        }

        _context = SynchronizationContext.Current;
        _timer = _time.CreateTimer(_ => Post(), null, FirstCheck, CheckPeriod);
        _gateway.StatusChanged += OnStatusChanged;
        _settings.Changed += OnSettingsChanged;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _gateway.StatusChanged -= OnStatusChanged;
        _settings.Changed -= OnSettingsChanged;
        _store.Changed -= OnStoreChanged;
        lock (_operationGate)
        {
            foreach (var operation in _operations.Values)
            {
                operation.Stop.Cancel();
            }
        }
    }

    /// <summary>Подписки, которым пора: успех старше промежутка из настроек, неудача — старше часа.</summary>
    public IReadOnlyList<ServerSubscription> Due()
    {
        if (_settings.Current.RefreshInterval is not { } interval)
        {
            return [];
        }

        var now = _time.GetUtcNow();
        return [.. _store.Subscriptions.Where(s => s.CanUpdate
            && (s.UpdatedAt is not { } updated || now - updated >= interval)
            && (s.LastAttemptAt is not { } attempt || now - attempt >= RetryAfterFailure))];
    }

    /// <summary>Обновить все подписки, которым пора, — если обновлять сейчас можно.</summary>
    public async Task UpdateDueAsync(CancellationToken cancellationToken = default)
    {
        if (Block != UpdateBlock.None)
        {
            return;
        }

        foreach (var subscription in Due())
        {
            var result = await UpdateAsync(subscription.Id, cancellationToken).ConfigureAwait(true);
            if (result.Failure is { } failure)
            {
                ScheduledUpdateFailed?.Invoke(this, new UpdateFailureNotice(subscription.Name, failure, result.StatusCode));
            }
        }
    }

    /// <summary>Обновить одну подписку: загрузить, разобрать, заменить список.</summary>
    public async Task<UpdateResult> UpdateAsync(string subscriptionId, CancellationToken cancellationToken = default)
    {
        if (Block is not UpdateBlock.None and var block)
        {
            return new UpdateResult(null, Block: block);
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_operationGate)
        {
            if (!_busy.Add(subscriptionId))
            {
                return new UpdateResult(null, Block: UpdateBlock.Busy);
            }
        }

        BusyChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            if (_store.SubscriptionUrl(subscriptionId) is not { } url)
            {
                return Fail(subscriptionId, DownloadFailure.NoAddress);
            }

            lock (_operationGate)
            {
                _operations[subscriptionId] = (url, stop);
            }

            string text;
            try
            {
                if (_settings.Current.UpdateOnlyViaVpn)
                {
                    var response = await _gateway.DownloadSubscriptionAsync(url, stop.Token).ConfigureAwait(true);
                    if (response.Failure is { } failure)
                    {
                        return IsCurrent(subscriptionId, url, stop) ? Fail(subscriptionId, failure, response.StatusCode)
                            : new UpdateResult(null, Block: UpdateBlock.Superseded);
                    }

                    text = response.Content is { Length: <= SubscriptionDownload.MaxBytes } content ? Encoding.UTF8.GetString(content) : string.Empty;
                }
                else
                {
                    text = (await _downloader.DownloadAsync(url, stop.Token).ConfigureAwait(true)).Text;
                }
            }
            catch (SubscriptionDownloadException e)
            {
                return IsCurrent(subscriptionId, url, stop) ? Fail(subscriptionId, e.Failure, e.StatusCode)
                    : new UpdateResult(null, Block: UpdateBlock.Superseded);
            }

            if (!IsCurrent(subscriptionId, url, stop))
            {
                return new UpdateResult(null, Block: UpdateBlock.Superseded);
            }

            List<ServerDefinition> servers = [.. ImportParser.Parse(text).Lines.Where(l => l.Result.IsOk).Select(l => l.Result.Server!)];
            if (servers.Count == 0)
            {
                return Fail(subscriptionId, DownloadFailure.NoServers);
            }

            var update = _store.TryApplyUpdate(subscriptionId, url, servers);
            if (update is null)
            {
                return new UpdateResult(null, Block: UpdateBlock.Superseded);
            }
            LogUpdated(_logger, update.Added, update.Removed, update.Kept);
            if (update.Selected != SelectedServerChange.None
                && _settings.Current.NotifySelectedServerChange
                && _store.Selected is { } selected
                && _store.Subscriptions.FirstOrDefault(s => s.Id == subscriptionId) is { } subscription)
            {
                SelectedServerChanged?.Invoke(this, new SelectedServerNotice(subscription.Name, selected.Name, update.Selected));
            }

            return new UpdateResult(update);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new UpdateResult(null, Block: UpdateBlock.Superseded);
        }
        finally
        {
            lock (_operationGate)
            {
                _operations.Remove(subscriptionId);
                _busy.Remove(subscriptionId);
            }
            BusyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool IsCurrent(string id, Uri address, CancellationTokenSource stop) =>
        !stop.IsCancellationRequested && _store.SubscriptionUrl(id) == address;

    private void OnStoreChanged(object? sender, EventArgs e)
    {
        List<(string Id, Uri Address, CancellationTokenSource Stop)> active;
        lock (_operationGate)
        {
            active = [.. _operations.Select(p => (p.Key, p.Value.Address, p.Value.Stop))];
        }

        foreach (var (id, address, stop) in active)
        {
            if (_store.SubscriptionUrl(id) != address)
            {
                try
                {
                    stop.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Операция завершилась между снимком и уведомлением.
                }
            }
        }
    }

    private UpdateResult Fail(string subscriptionId, DownloadFailure failure, int? statusCode = null)
    {
        LogFailed(_logger, failure, statusCode);
        _store.MarkUpdateFailed(subscriptionId, failure, statusCode);
        return new UpdateResult(null, failure, statusCode);
    }

    private void OnStatusChanged(object? sender, ConnectionStatus status)
    {
        if (status.State == ConnectionState.Connected)
        {
            Post();
        }
    }

    private void OnSettingsChanged(object? sender, Core.Settings.AppSettings settings) => Post();

    /// <summary>Проверка по расписанию — в контексте окна: список серверов меняется там же, где его показывают.</summary>
    private void Post()
    {
        if (_context is { } context)
        {
            context.Post(_ => _ = RunDueAsync(), null);
        }
        else
        {
            _ = RunDueAsync();
        }
    }

    // Фоновая попытка не должна ронять программу: сбой — в журнал, прошлый список остается.
    private async Task RunDueAsync()
    {
        try
        {
            await UpdateDueAsync().ConfigureAwait(true);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or ArgumentException or System.Data.Common.DbException)
        {
            LogCrashed(_logger, e.GetType().Name);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Подписка обновлена: новых {Added}, удалено {Removed}, осталось {Kept}")]
    private static partial void LogUpdated(ILogger logger, int added, int removed, int kept);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Подписка не обновилась: {Failure} {StatusCode} — прошлый список оставлен")]
    private static partial void LogFailed(ILogger logger, DownloadFailure failure, int? statusCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "Обновление подписок по расписанию прервано: {Error}")]
    private static partial void LogCrashed(ILogger logger, string error);
}
