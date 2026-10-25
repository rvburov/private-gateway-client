using System.Collections.Concurrent;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Вывод ядер туннеля в тесте — последние строки. Запрос через туннель упал — они идут в сообщение ошибки: по прогону
/// CI видно, на каком звене встало соединение (TUN, Xray, сервер, прямой выход).
/// </summary>
internal sealed class CoreOutput
{
    private const int Capacity = 300;
    private readonly ConcurrentQueue<string> _lines = new();

    public void Add(string source, string line)
    {
        _lines.Enqueue($"{source}: {line}");
        while (_lines.Count > Capacity && _lines.TryDequeue(out _))
        {
        }
    }

    /// <summary>Выполнить запрос через туннель; упал — та же ошибка с выводом ядер.</summary>
    public async Task<T> TraceAsync<T>(Func<Task<T>> request)
    {
        try
        {
            return await request().ConfigureAwait(false);
        }
        catch (InvalidOperationException e)
        {
            throw new InvalidOperationException($"{e.Message}\nВывод ядер (последние строки):\n{string.Join('\n', _lines)}", e);
        }
    }
}
