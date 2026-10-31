using PrivateGateway.Client.Core;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Сколько кругов до сервера уходит на новое соединение — из замеров «Задержки» (решение 509).</summary>
public sealed class DelayBreakdownTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Rounds_come_from_the_measurements()
    {
        new DelayBreakdown(120, 124, 480, At).Rounds.ShouldBe(4.0, "480 мс нового соединения при круге в 120 мс — четыре круга");
        new DelayBreakdown(31, 34, 127, At).Rounds.ShouldBe(4.1);
        new DelayBreakdown(null, 124, 480, At).Rounds.ShouldBeNull("до сервера не измерено (сервер по UDP) — кругов не посчитать");
        new DelayBreakdown(120, 124, null, At).Rounds.ShouldBeNull();
    }
}
