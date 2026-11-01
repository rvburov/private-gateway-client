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

    /// <summary>
    /// Кандидат, свободный для TCP и UDP в момент проверки: вход SOCKS5 ядра занимает оба. Проверочные сокеты закрываются
    /// до возврата; это не межпроцессное резервирование, и другой процесс может занять порт до запуска входа ядра.
    /// </summary>
    public static int FreeForTcpAndUdp() => FreeForTcpAndUdp(Random.Shared);

    /// <summary>То же со своим источником случайных чисел (тесты).</summary>
    public static int FreeForTcpAndUdp(Random random)
    {
        using var lease = ReserveForTcpAndUdp(random);
        return lease.Port;
    }

    /// <summary>
    /// Выбрать порт и удерживать слушателем TCP и сокетом UDP до <see cref="LocalPortLease.Dispose"/>. Ядро сможет занять порт только
    /// после освобождения пары: передача номера внешнему процессу по-прежнему не является атомарным резервированием.
    /// </summary>
    public static LocalPortLease ReserveForTcpAndUdp() => ReserveForTcpAndUdp(Random.Shared);

    /// <summary>Удерживать конкретный локальный порт до запуска входа; занятый порт дает SocketException.</summary>
    public static LocalPortLease ReserveForTcpAndUdp(int port)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        return BindBoth(ProtocolType.Tcp, ProtocolType.Udp, port);
    }

    /// <summary>То же со своим источником случайных чисел (тесты).</summary>
    public static LocalPortLease ReserveForTcpAndUdp(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        for (var attempt = 0; attempt < 500; attempt++)
        {
            var port = random.Next(First, End);
            if (Issue(port) && TryReserve(port) is { } lease)
            {
                return lease;
            }
        }

        return FromSystem();
    }

    /// <summary>
    /// Запасной путь, если весь диапазон занят: порт дает ОС. На Windows списки зарезервированных портов у TCP и UDP свои —
    /// порт, который ОС дала для TCP, для UDP бывает закрыт, и наоборот. Поэтому порт по очереди выбирает то TCP, то UDP, а
    /// другой протокол его проверяет.
    /// </summary>
    private static LocalPortLease FromSystem()
    {
        for (var attempt = 1; ; attempt++)
        {
            var (first, second) = attempt % 2 == 1 ? (ProtocolType.Tcp, ProtocolType.Udp) : (ProtocolType.Udp, ProtocolType.Tcp);
            try
            {
                var lease = BindBoth(first, second, 0);
                if (Issue(lease.Port) || attempt >= 200)
                {
                    return lease;
                }

                lease.Dispose();
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

    private static LocalPortLease? TryReserve(int port)
    {
        try
        {
            return BindBoth(ProtocolType.Tcp, ProtocolType.Udp, port);
        }
        catch (SocketException)
        {
            return null;
        }
    }

    private static LocalPortLease BindBoth(ProtocolType first, ProtocolType second, int port)
    {
        var chosen = Bind(first, port);
        Socket? other = null;
        try
        {
            var actual = ((IPEndPoint)chosen.LocalEndPoint!).Port;
            other = Bind(second, actual);
            return new LocalPortLease(actual, chosen, other);
        }
        catch
        {
            other?.Dispose();
            chosen.Dispose();
            throw;
        }
    }

    private static Socket Bind(ProtocolType protocol, int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, protocol == ProtocolType.Tcp ? SocketType.Stream : SocketType.Dgram, protocol);
        try
        {
            socket.ExclusiveAddressUse = true;
            socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            if (protocol == ProtocolType.Tcp)
            {
                // TCP только с Bind на Unix допускает другой Bind: .NET включает SO_REUSEADDR для TCP.
                // Удерживаем возможность запустить слушатель, а не только привязать адрес сокета.
                socket.Listen(1);
            }

            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
