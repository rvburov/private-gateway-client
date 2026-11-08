using System.Text;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Servers;

namespace PrivateGateway.Client.Application.Import;

/// <summary>Импорт и обновление: выбранный путь сохраняется на все попытки, при смене сессии загрузка отменяется.</summary>
public sealed class SessionSubscriptionDownloader(ISubscriptionDownloader direct, IGatewayClient gateway, TimeProvider time, bool requireVpn = false) : ISubscriptionDownloader
{
    public const int MaxAttempts = 3;
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(6);

    public async Task<SubscriptionContent> DownloadAsync(Uri url, CancellationToken cancellationToken = default)
    {
        var initial = gateway.Status;
        var throughCore = initial.State == ConnectionState.Connected && !initial.IsPaused;
        if ((requireVpn && (!throughCore || initial.Mode != ConnectionMode.Vpn))
            || (initial.State != ConnectionState.Off && !throughCore))
        {
            gateway.ReportSubscriptionAttempt(DownloadFailure.Connection, null, 0, TimeSpan.Zero, false);
            throw new SubscriptionDownloadException(DownloadFailure.Connection);
        }

        using var changed = new CancellationTokenSource();
        using var total = new CancellationTokenSource(SubscriptionDownload.Timeout, time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, changed.Token, total.Token);
        void StatusChanged(object? sender, ConnectionStatus status)
        {
            if (!SameSession(initial, status))
            {
                try
                {
                    changed.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Уведомление могло совпасть с завершением загрузки.
                }
            }
        }

        gateway.StatusChanged += StatusChanged;
        var started = time.GetTimestamp();
        SubscriptionDownloadException? last = null;
        try
        {
            StatusChanged(null, gateway.Status);
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                stop.Token.ThrowIfCancellationRequested();
                using var deadline = new CancellationTokenSource(AttemptTimeout, time);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, deadline.Token);
                var at = time.GetTimestamp();
                try
                {
                    SubscriptionContent content;
                    if (throughCore)
                    {
                        var result = await gateway.DownloadSubscriptionAsync(url, request.Token).ConfigureAwait(false);
                        if (result.Failure is { } failure)
                        {
                            throw new SubscriptionDownloadException(failure, result.StatusCode, retryAfter: result.RetryAfter);
                        }

                        if (result.Content is not { Length: <= SubscriptionDownload.MaxBytes } bytes)
                        {
                            throw new SubscriptionDownloadException(DownloadFailure.TooLarge);
                        }

                        content = new SubscriptionContent(Encoding.UTF8.GetString(bytes), SubscriptionContent.TitleFrom(result.Title));
                    }
                    else
                    {
                        content = await direct.DownloadAsync(url, request.Token).ConfigureAwait(false);
                    }

                    request.Token.ThrowIfCancellationRequested();
                    if (!SameSession(initial, gateway.Status))
                    {
                        throw new SubscriptionDownloadException(DownloadFailure.Connection);
                    }

                    gateway.ReportSubscriptionAttempt(null, null, attempt, time.GetElapsedTime(at), throughCore);
                    return content;
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested && !stop.IsCancellationRequested)
                {
                    last = new SubscriptionDownloadException(DownloadFailure.Timeout);
                }
                catch (SubscriptionDownloadException error)
                {
                    last = error;
                }

                gateway.ReportSubscriptionAttempt(last!.Failure, last.StatusCode, attempt, time.GetElapsedTime(at), throughCore);
                var delay = last.RetryAfter ?? TimeSpan.FromMilliseconds(250 * attempt);
                if (delay < TimeSpan.Zero)
                {
                    delay = TimeSpan.Zero;
                }

                if (attempt == MaxAttempts || !CanRetry(last.Failure, last.StatusCode)
                    || time.GetElapsedTime(started) + delay >= SubscriptionDownload.Timeout)
                {
                    throw last;
                }

                await Task.Delay(delay, time, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var failure = changed.IsCancellationRequested ? DownloadFailure.Connection : DownloadFailure.Timeout;
            gateway.ReportSubscriptionAttempt(failure, null, 0, time.GetElapsedTime(started), throughCore);
            throw new SubscriptionDownloadException(failure);
        }
        finally
        {
            gateway.StatusChanged -= StatusChanged;
        }

        throw last!;
    }

    public static bool CanRetry(DownloadFailure failure, int? statusCode) => failure is DownloadFailure.Timeout
        or DownloadFailure.Connection or DownloadFailure.Network or DownloadFailure.NameResolution
        || failure == DownloadFailure.Status && statusCode is 408 or 429 or 500 or 502 or 503 or 504;

    private static bool SameSession(ConnectionStatus before, ConnectionStatus after) =>
        (before.State, before.Mode, before.ServerId, before.Since, before.IsPaused)
        == (after.State, after.Mode, after.ServerId, after.Since, after.IsPaused);
}
