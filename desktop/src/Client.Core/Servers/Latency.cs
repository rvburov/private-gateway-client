namespace PrivateGateway.Client.Core.Servers;

/// <summary>Почему у сервера нет задержки.</summary>
public enum LatencyFailure
{
    /// <summary>Ядро Xray за этот сервер не возьмется (Hysteria2, TUIC, нет ключа…).</summary>
    Unsupported,

    /// <summary>Через сервер не пришел ответ за отведенное время.</summary>
    NoResponse,

    /// <summary>Ядро не запустилось или не открыло входы — проверка не состоялась.</summary>
    CoreFailed,
}

/// <summary>Задержка через сервер в миллисекундах или причина, почему ее нет.</summary>
public sealed record LatencyResult(int? Milliseconds, LatencyFailure? Failure)
{
    public static LatencyResult Of(TimeSpan elapsed) => new((int)Math.Max(1, Math.Round(elapsed.TotalMilliseconds)), null);

    public static LatencyResult Failed(LatencyFailure failure) => new(null, failure);
}
