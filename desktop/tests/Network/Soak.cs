using System.Diagnostics;
using System.Globalization;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Долгий прогон (soak): сбои по кругу, пока не выйдет время из <see cref="Variable"/>. Не задано — тесты soak пропускаются:
/// в обычный прогон они не входят (минуты на круг), идут ночью (workflow soak.yml) и по scripts/soak.sh.
/// </summary>
internal static class Soak
{
    public const string Variable = "PG_SOAK_MINUTES";

    /// <summary>Сколько крутить сбои; не задано — тест пропускается.</summary>
    public static TimeSpan Duration()
    {
        var value = Environment.GetEnvironmentVariable(Variable);
        Assert.SkipWhen(!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) || minutes <= 0,
            $"долгий прогон — по {Variable}=<минут> (scripts/soak.sh)");
        return TimeSpan.FromMinutes(minutes);
    }
}

/// <summary>
/// Ресурсы процесса теста (служба работает в нем) и машины: дескрипторы, потоки, управляемая память, живые ядра. Круг за
/// кругом они не должны расти: утечка сокета, таймера или процесса ядра на каждом сбое за ночь съест машину.
/// </summary>
internal sealed record ResourceSample(int Handles, int Threads, long ManagedBytes, int Xray, int SingBox)
{
    public static ResourceSample Take()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var self = Process.GetCurrentProcess();
        return new(self.HandleCount, self.Threads.Count, GC.GetTotalMemory(forceFullCollection: true), Count("xray"), Count("sing-box"));
    }

    /// <summary>Рост с <paramref name="start"/> не больше разумного: дескрипторов и потоков — на десятки, памяти — на 64 МБ, ядер — ни одного.</summary>
    public void ShouldNotGrowFrom(ResourceSample start)
    {
        ArgumentNullException.ThrowIfNull(start);
        (Handles - start.Handles).ShouldBeLessThan(150, $"дескрипторы: {start.Handles} → {Handles}");
        (Threads - start.Threads).ShouldBeLessThan(40, $"потоки: {start.Threads} → {Threads}");
        (ManagedBytes - start.ManagedBytes).ShouldBeLessThan(64L << 20, $"управляемая память: {start.ManagedBytes} → {ManagedBytes}");
        (Xray, SingBox).ShouldBe((start.Xray, start.SingBox), "живых ядер столько же: ни одно не осталось сиротой");
    }

    private static int Count(string name)
    {
        var processes = Process.GetProcessesByName(name);
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return processes.Length;
    }
}

/// <summary>
/// Сторож обхода туннеля: каждые 250 мс запрос из пространства стенда к сайту в локальной сети машины. Через туннель он не
/// проходит никогда (Xray закрывает частные сети), блок при обрыве его тоже не пропускает — ответил сайт, значит, трафик
/// ушел мимо туннеля. Вне окон, где это разрешено (<see cref="Allow"/>: защиту выключили, пауза для входа в Wi-Fi, правила
/// сняла другая программа), — утечка.
/// </summary>
internal sealed class LeakSentinel : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly List<string> _leaks = [];
    private readonly Task _loop;
    private int _allowed;
    private int _probes;

    public LeakSentinel(NetworkNamespace netns, Uri lan, string page) => _loop = Task.Run(() => LoopAsync(netns, lan, page));

    /// <summary>Что сейчас делает прогон — пишется рядом с утечкой.</summary>
    public string Activity { get; set; } = "подключение";

    /// <summary>Сколько запросов сделано.</summary>
    public int Probes => Volatile.Read(ref _probes);

    /// <summary>Запросы мимо туннеля, которых быть не должно: время и шаг прогона.</summary>
    public IReadOnlyList<string> Leaks
    {
        get
        {
            lock (_leaks)
            {
                return [.. _leaks];
            }
        }
    }

    /// <summary>Окно, где мимо туннеля можно: пока не закрыто.</summary>
    public IDisposable Allow()
    {
        Interlocked.Increment(ref _allowed);
        return new Window(() => Interlocked.Decrement(ref _allowed));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await _loop;
        _stop.Dispose();
    }

    private async Task LoopAsync(NetworkNamespace netns, Uri lan, string page)
    {
        while (!_stop.IsCancellationRequested)
        {
            // Ответ, пришедший на границе окна, — не утечка: окно было открыто в начале или в конце запроса.
            var allowedBefore = Volatile.Read(ref _allowed) > 0;
            var activity = Activity;
            bool direct;
            try
            {
                direct = await netns.CurlAsync(lan, seconds: 1) == page;
            }
            catch (InvalidOperationException)
            {
                direct = false;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Interlocked.Increment(ref _probes);
            if (direct && !allowedBefore && Volatile.Read(ref _allowed) == 0)
            {
                lock (_leaks)
                {
                    _leaks.Add($"{DateTime.UtcNow:HH:mm:ss.fff} — {activity}");
                }
            }

            try
            {
                await Task.Delay(250, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private sealed class Window(Action close) : IDisposable
    {
        private int _closed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) == 0)
            {
                close();
            }
        }
    }
}
