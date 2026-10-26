namespace PrivateGateway.Client.Core.Platform;

/// <summary>Сон по двум счетчикам ОС с загрузки: одному со сном, другому без него (часы <see cref="ISuspendClock"/>).</summary>
public static class SuspendTime
{
    /// <summary>
    /// Сколько машина спала: счетчик со сном минус счетчик без сна, в единицах ОС (<paramref name="numerator"/> /
    /// <paramref name="denominator"/> наносекунды на единицу). Счетчик без сна читать первым: второй за это время только
    /// уходит вперед. Если ОС все же отдала со сном меньше (один такт, машина не спала), сна нет — а не века, которые дает
    /// переполнение беззнакового вычитания и которые служба приняла бы за пробуждение.
    /// </summary>
    public static TimeSpan Between(ulong withoutSleep, ulong withSleep, uint numerator, uint denominator)
    {
        ArgumentOutOfRangeException.ThrowIfZero(denominator);
        return withSleep <= withoutSleep
            ? TimeSpan.Zero
            : TimeSpan.FromTicks((long)((UInt128)(withSleep - withoutSleep) * numerator / denominator / 100));
    }
}
