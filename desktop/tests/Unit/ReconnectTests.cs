using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Переподключение к выбранному серверу: пауза вдвое больше, но не больше потолка; когда сдаться — по политике.</summary>
public sealed class ReconnectTests
{
    [Fact]
    public void Pause_doubles_up_to_the_ceiling()
    {
        Enumerable.Range(1, 9).Select(a => Reconnect.Delay(a, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60)).TotalSeconds)
            .ShouldBe([1, 2, 4, 8, 16, 32, 60, 60, 60]);
        Reconnect.Delay(int.MaxValue, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60)).ShouldBe(TimeSpan.FromSeconds(60), "и через много попыток — без переполнения");
        Should.Throw<ArgumentOutOfRangeException>(() => Reconnect.Delay(0, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60)));
    }

    [Theory]
    [InlineData(ReconnectPolicy.Never, 0, true)]
    [InlineData(ReconnectPolicy.UpTo5Minutes, 299, false)]
    [InlineData(ReconnectPolicy.UpTo5Minutes, 300, true)]
    [InlineData(ReconnectPolicy.Backoff60Seconds, 3600, false)]
    public void Service_gives_up_only_by_the_policy(ReconnectPolicy policy, int secondsSinceLost, bool givesUp) =>
        Reconnect.GivesUp(policy, TimeSpan.FromSeconds(secondsSinceLost), TimeSpan.FromMinutes(5)).ShouldBe(givesUp);
}
