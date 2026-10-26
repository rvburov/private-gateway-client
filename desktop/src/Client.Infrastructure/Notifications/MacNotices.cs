using PrivateGateway.Client.Application.Notifications;

namespace PrivateGateway.Client.Infrastructure.Notifications;

/// <summary>
/// Уведомления macOS без вызовов ОС: имена категорий (набор кнопок) и действий, ответ на нажатие, уровень вмешательства —
/// то, что одинаково на любой машине и проверяется тестами без macOS.
/// </summary>
public static class MacNotices
{
    /// <summary>Нажали само уведомление (UNNotificationDefaultActionIdentifier).</summary>
    public const string DefaultAction = "com.apple.UNNotificationDefaultActionIdentifier";

    /// <summary>Уведомление смахнули (UNNotificationDismissActionIdentifier): это не нажатие.</summary>
    public const string DismissAction = "com.apple.UNNotificationDismissActionIdentifier";

    private const string ActionPrefix = "pg.action.";

    /// <summary>Уровни вмешательства (UNNotificationInterruptionLevel): тихое — только в Центре уведомлений, важное — со звуком.</summary>
    public const int Passive = 0;

    public const int Active = 1;

    /// <summary>Категория — набор кнопок по порядку: у одинаковых наборов одна категория. Без кнопок — null.</summary>
    public static string? Category(IReadOnlyList<NoticeButton> buttons)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        return buttons.Count == 0 ? null : "pg.buttons." + string.Join('.', buttons.Select(b => b.Action));
    }

    /// <summary>Имя действия кнопки.</summary>
    public static string Action(NoticeAction action) => ActionPrefix + action;

    /// <summary>
    /// Ответ на нажатие: уведомление <paramref name="identifier"/> (его метка), действие <paramref name="action"/>; само
    /// уведомление — без действия, смахнули или действие не наше — null.
    /// </summary>
    public static NoticeReply? Reply(string? identifier, string? action)
    {
        if (string.IsNullOrEmpty(identifier) || action is null or DismissAction)
        {
            return null;
        }

        if (action == DefaultAction)
        {
            return new NoticeReply(identifier, null);
        }

        return action.StartsWith(ActionPrefix, StringComparison.Ordinal) && Enum.TryParse<NoticeAction>(action[ActionPrefix.Length..], out var parsed)
            && Enum.IsDefined(parsed)
            ? new NoticeReply(identifier, parsed)
            : null;
    }

    /// <summary>Важное — со звуком и баннером; остальное — тихо, в Центр уведомлений (и «Не беспокоить» соблюдается всегда).</summary>
    public static int InterruptionLevel(bool important) => important ? Active : Passive;
}
