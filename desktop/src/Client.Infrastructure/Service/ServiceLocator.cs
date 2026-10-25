using PrivateGateway.Client.Contracts;

namespace PrivateGateway.Client.Infrastructure.Service;

/// <summary>Адрес службы и какая она: установленная (<paramref name="System"/>) или служба пользователя.</summary>
public sealed record ServiceAddress(string Endpoint, bool System);

/// <summary>
/// Поиск службы при запуске интерфейса: сначала установленная, потом служба пользователя. Нашлась —
/// интерфейс работает через нее; нет — на имитации (значок «демо»), пока не появится экран «Служба
/// не запущена» (доска 16).
/// </summary>
public static class ServiceLocator
{
    /// <summary>Сколько ждать каждый адрес: служба на этой же машине отвечает сразу.</summary>
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(300);

    /// <summary>Где искать на этой ОС — по порядку.</summary>
    public static IReadOnlyList<ServiceAddress> Candidates =>
        [new(IpcEndpoints.System, System: true), new(IpcEndpoints.User, System: false)];

    /// <summary>Первая служба, которая ответила; null — ни одной.</summary>
    public static ServiceAddress? Find(IReadOnlyList<ServiceAddress> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        return candidates.FirstOrDefault(c => IpcChannel.Answers(c.Endpoint, c.System, ProbeTimeout));
    }
}
