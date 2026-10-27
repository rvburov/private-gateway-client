[assembly: AssemblyFixture(typeof(PrivateGateway.Client.Tests.Network.ThreadPoolFloor))]

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Нижняя граница пула потоков на весь прогон сетевых тестов. В начале прогона десятки тестов стартуют разом, и часть из
/// них ждет процессы и сокеты синхронно; пул добавляет потоки по одному в полсекунды, и тесты со сроком в секунды (замер
/// задержки, STUN) на Windows CI успевали истечь, ничего не начав (прогоны 291, 306, 319: 7 с вместо 3). С запасом
/// потоков с первой секунды сроки тестов меряют сеть, а не очередь пула.
/// </summary>
public sealed class ThreadPoolFloor
{
    /// <summary>Сколько потоков пула держать готовыми — с запасом на все тесты, идущие одновременно.</summary>
    public const int Threads = 64;

    public ThreadPoolFloor()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, Threads), Math.Max(io, Threads));
    }
}
