using System.Net;
using System.Net.Sockets;

namespace PrivateGateway.Client.Service.Cores;

/// <summary>
/// Свободный порт на 127.0.0.1 для входа ядра (решение 478). Порт выбирается заранее, а ядро занимает его позже — после
/// запуска процесса, а у ретранслятора sing-box и через секунды. Порт из временных портов ОС за это время мог взять себе
/// любой исходящий сокет — например, соединение браузера с прокси на 127.0.0.1, — и ядро получало «address already in use»
/// (Linux) или «forbidden by its access permissions» (Windows). Поэтому порт — из <see cref="First"/>–<see cref="End"/>,
/// ниже временных портов Windows и macOS (49152–65535) и Linux (32768–60999).
/// </summary>
public static class LocalPort
{
    /// <summary>Первый порт диапазона.</summary>
    public const int First = 20000;

    /// <summary>Порт за последним в диапазоне.</summary>
    public const int End = 32000;

    /// <summary>
    /// Сколько последних выданных портов служба не выдает снова: порт, выданный одному входу, но еще не занятый ядром, второй
    /// раз не достанется. Замер задержки берет по порту на сервер разом — до этого числа входов за один запуск ядра повторов нет.
    /// </summary>
    public const int Remembered = 4096;

    private static readonly Lock Gate = new();
    private static readonly Queue<int> Issued = new();
    private static readonly HashSet<int> IssuedSet = [];

    /// <summary>Порт, свободный и для TCP, и для UDP: вход SOCKS5 ядра занимает оба.</summary>
    public static int FreeForTcpAndUdp() => FreeForTcpAndUdp(Random.Shared);

    /// <summary>То же со своим источником случайных чисел (тесты).</summary>
    public static int FreeForTcpAndUdp(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var port = random.Next(First, End);
            if (Issue(port) && Free(port))
            {
                return port;
            }
        }

        return FromSystem();
    }

    /// <summary>
    /// Запасной путь, если весь диапазон занят: порт дает ОС. На Windows списки зарезервированных портов у TCP и UDP свои —
    /// порт, который ОС дала для TCP, для UDP бывает закрыт, и наоборот. Поэтому порт по очереди выбирает то TCP, то UDP, а
    /// другой протокол его проверяет.
    /// </summary>
    private static int FromSystem()
    {
        for (var attempt = 1; ; attempt++)
        {
            var (first, second) = attempt % 2 == 1 ? (ProtocolType.Tcp, ProtocolType.Udp) : (ProtocolType.Udp, ProtocolType.Tcp);
            try
            {
                int port;
                using (var chosen = Bind(first, 0))
                {
                    port = ((IPEndPoint)chosen.LocalEndPoint!).Port;
                    using var other = Bind(second, port);
                }

                if (Issue(port) || attempt >= 200)
                {
                    return port;
                }
            }
            catch (SocketException) when (attempt < 200)
            {
                // Порт закрыт или занят для другого протокола — следующий.
            }
        }
    }

    /// <summary>Записать порт выданным; false — он уже выдан недавно.</summary>
    private static bool Issue(int port)
    {
        lock (Gate)
        {
            if (!IssuedSet.Add(port))
            {
                return false;
            }

            Issued.Enqueue(port);
            if (Issued.Count > Remembered)
            {
                IssuedSet.Remove(Issued.Dequeue());
            }

            return true;
        }
    }

    private static bool Free(int port)
    {
        try
        {
            using var tcp = Bind(ProtocolType.Tcp, port);
            using var udp = Bind(ProtocolType.Udp, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static Socket Bind(ProtocolType protocol, int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, protocol == ProtocolType.Tcp ? SocketType.Stream : SocketType.Dgram, protocol);
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
