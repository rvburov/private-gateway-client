using PrivateGateway.Client.Contracts;

namespace PrivateGateway.Client.Infrastructure.Service;

/// <summary>Адрес службы и какая она: установленная (<paramref name="System"/>) или служба пользователя.</summary>
public sealed record ServiceAddress(string Endpoint, bool System);

/// <summary>
/// Где окно ищет службу: сначала установленную (у нее есть режим VPN), потом службу пользователя.
/// Службы пользователя нет, а нужен режим прокси, — окно запускает ее само и ждет, пока она ответит.
/// </summary>
/// <param name="candidates">Адреса по порядку.</param>
/// <param name="answers">Отвечает ли служба по адресу — и служба ли это (<see cref="IpcChannel.Answers"/>).</param>
/// <param name="launcher">Запуск службы пользователя.</param>
/// <param name="time">Часы для ожидания запуска.</param>
public sealed class ServiceLocator(IReadOnlyList<ServiceAddress> candidates, Func<ServiceAddress, bool> answers, IUserServiceLauncher launcher, TimeProvider time)
{
    /// <summary>Сколько ждать каждый адрес: служба на этой же машине отвечает сразу.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>Сколько ждать, пока запущенная служба пользователя ответит.</summary>
    public static readonly TimeSpan UserServiceStart = TimeSpan.FromSeconds(10);

    /// <summary>Адреса этой ОС по порядку: установленная служба, служба пользователя.</summary>
    public static IReadOnlyList<ServiceAddress> Candidates =>
        [new(IpcEndpoints.System, System: true), new(IpcEndpoints.User, System: false)];

    /// <summary>Поиск службы для этого окна: адреса этой ОС, настоящий канал, служба рядом с программой.</summary>
    public static ServiceLocator ForThisWindow(TimeProvider time) =>
        new(Candidates, a => IpcChannel.Answers(a.Endpoint, a.System, ProbeTimeout), UserServiceLauncher.ForThisWindow(), time);

    /// <summary>Первая служба, которая ответила; null — ни одной.</summary>
    public ServiceAddress? Find() => candidates.FirstOrDefault(answers);

    /// <summary>
    /// Запустить службу пользователя и дождаться ответа по ее адресу. Launched — процесс запущен;
    /// Address — служба ответила (null — файла службы нет или она не ответила за <see cref="UserServiceStart"/>).
    /// </summary>
    public async Task<(ServiceAddress? Address, bool Launched)> StartUserServiceAsync(CancellationToken cancellationToken = default)
    {
        if (candidates.FirstOrDefault(c => !c.System) is not { } user || !launcher.Start())
        {
            return (null, false);
        }

        var started = time.GetTimestamp();
        do
        {
            if (await Task.Run(() => answers(user), cancellationToken).ConfigureAwait(false))
            {
                return (user, true);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), time, cancellationToken).ConfigureAwait(false);
        }
        while (time.GetElapsedTime(started) < UserServiceStart);

        return (null, true);
    }
}
