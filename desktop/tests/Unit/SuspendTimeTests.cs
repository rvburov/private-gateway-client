using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Сон по двум счетчикам ОС (<see cref="SuspendTime"/>): часы сна macOS, Windows и Linux считают через него.</summary>
public class SuspendTimeTests
{
    [Fact]
    public void Counter_with_sleep_a_tick_behind_means_no_sleep_and_not_centuries()
    {
        // Машина не спала, счетчики равны, а второй прочитан на такт раньше: беззнаковое вычитание дало бы 2^64 − 1.
        SuspendTime.Between(withoutSleep: 1_000_001, withSleep: 1_000_000, numerator: 125, denominator: 3).ShouldBe(TimeSpan.Zero);
        SuspendTime.Between(5, 5, 1, 1).ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public void Units_of_each_os_turn_into_time()
    {
        // macOS на Apple Silicon: такт 125/3 нс (24 МГц) — 24 млн тактов в секунде.
        SuspendTime.Between(0, 24_000_000, 125, 3).ShouldBe(TimeSpan.FromSeconds(1));
        // Windows: единица — 100 нс.
        SuspendTime.Between(10_000_000, 10_000_000 + 600_000_000, 100, 1).ShouldBe(TimeSpan.FromMinutes(1));
        // Linux: наносекунды.
        SuspendTime.Between(7, 7 + 3_600_000_000_000, 1, 1).ShouldBe(TimeSpan.FromHours(1));
    }

    [Fact]
    public void Long_sleep_does_not_overflow()
    {
        // Тысяча лет в тактах macOS (24 МГц): такты × 125 уже не помещаются в 64 бита.
        const ulong Ticks = 24_000_000UL * 60 * 60 * 24 * 365 * 1000;

        SuspendTime.Between(0, Ticks, 125, 3).ShouldBe(TimeSpan.FromDays(365 * 1000));
    }
}
