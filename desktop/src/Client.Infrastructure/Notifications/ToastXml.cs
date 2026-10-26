using System.Xml.Linq;
using PrivateGateway.Client.Application.Notifications;

namespace PrivateGateway.Client.Infrastructure.Notifications;

/// <summary>
/// Разметка уведомления Windows и разбор аргументов нажатия: в аргументах — кнопка и метка уведомления.
/// </summary>
public static class ToastXml
{
    /// <summary>Разметка уведомления: заголовок, текст, до двух кнопок, звук только у важного.</summary>
    public static string Content(string tag, NoticeText text, bool important)
    {
        ArgumentNullException.ThrowIfNull(text);
        var toast = new XElement("toast", new XAttribute("launch", Arguments(tag, null)),
            new XElement("visual", new XElement("binding", new XAttribute("template", "ToastGeneric"),
                new XElement("text", text.Title), new XElement("text", text.Body))));
        if (text.Buttons.Count > 0)
        {
            toast.Add(new XElement("actions", text.Buttons.Take(2).Select(b => new XElement("action",
                new XAttribute("content", b.Label), new XAttribute("arguments", Arguments(tag, b.Action)), new XAttribute("activationType", "foreground")))));
        }

        toast.Add(important
            ? new XElement("audio", new XAttribute("src", "ms-winsoundevent:Notification.Default"))
            : new XElement("audio", new XAttribute("silent", "true")));
        return toast.ToString(SaveOptions.DisableFormatting);
    }

    /// <summary>Ответ из аргументов нажатия: метка и кнопка (нет кнопки — нажато само уведомление).</summary>
    public static NoticeReply? Reply(string arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var parts = arguments.Split('&').Select(p => p.Split('=', 2)).Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]), StringComparer.Ordinal);
        if (!parts.TryGetValue("tag", out var tag) || tag.Length == 0)
        {
            return null;
        }

        return new NoticeReply(tag, parts.TryGetValue("action", out var action) && Enum.TryParse<NoticeAction>(action, out var parsed) ? parsed : null);
    }

    private static string Arguments(string tag, NoticeAction? action) => $"action={action}&tag={Uri.EscapeDataString(tag)}";
}
