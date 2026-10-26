using System.Net;

namespace PrivateGateway.Client.Core.Platform;

/// <summary>
/// DNS ОС для туннеля там, где sing-box его сам не прописывает (macOS): пока туннель поднят, ОС спрашивает DNS туннеля,
/// а не роутер — иначе запросы к роутеру в локальной сети шли бы мимо туннеля, а под блоком при обрыве не шли бы вовсе.
/// На Windows и Linux DNS прописывает sing-box, там этого нет.
/// </summary>
public interface ISystemDns
{
    /// <summary>
    /// Направить DNS основной сети ОС на <paramref name="server"/>; уже направлен — сверить: сменилась основная сеть —
    /// прежней вернуть ее DNS, новую направить.
    /// </summary>
    /// <exception cref="IOException">ОС не дала поменять DNS.</exception>
    void Apply(IPAddress server);

    /// <summary>Вернуть DNS, как был до <see cref="Apply"/>, — и после аварии службы; менять нечего — ничего.</summary>
    /// <exception cref="IOException">ОС не дала вернуть DNS.</exception>
    void Restore();
}
