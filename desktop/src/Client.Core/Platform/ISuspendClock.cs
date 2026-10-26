namespace PrivateGateway.Client.Core.Platform;

/// <summary>
/// Сколько машина провела во сне с загрузки. Растет только во сне: по его росту служба узнает, что машина спала, — таймеры
/// .NET во сне стоят и сами этого не скажут.
/// </summary>
public interface ISuspendClock
{
    TimeSpan Suspended { get; }
}
