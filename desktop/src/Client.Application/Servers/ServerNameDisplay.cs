using System.Globalization;
using System.Text;

namespace PrivateGateway.Client.Application.Servers;

/// <summary>
/// Отступы вокруг эмодзи только для показа названия. Исходное имя подписки, поиск, переименование и экспорт не меняются.
/// Флаг, знак с модификатором кожи и последовательность ZWJ остаются одним текстовым элементом.
/// </summary>
public static class ServerNameDisplay
{
    public static string Format(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var result = new StringBuilder(name.Length);
        var elements = StringInfo.GetTextElementEnumerator(name);
        var previous = ElementKind.Other;
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var current = Kind(element);
            if ((previous == ElementKind.Emoji && current is ElementKind.Emoji or ElementKind.Word)
                || (previous == ElementKind.Word && current == ElementKind.Emoji))
            {
                result.Append(' ');
            }

            result.Append(element);
            previous = current;
        }

        return result.ToString();
    }

    private static ElementKind Kind(string element)
    {
        if (!Rune.TryGetRuneAt(element, 0, out var first))
        {
            return ElementKind.Other;
        }

        return first.Value is >= 0x1F000 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF
            or >= 0x231A and <= 0x231B or >= 0x23E9 and <= 0x23EC or 0x23F0 or 0x23F3
            or >= 0x25FD and <= 0x25FE or 0x2B50 or 0x2B55
            || element.Contains('\uFE0F', StringComparison.Ordinal) || element.Contains('\u20E3', StringComparison.Ordinal)
            ? ElementKind.Emoji : Rune.IsLetterOrDigit(first) ? ElementKind.Word : ElementKind.Other;
    }

    private enum ElementKind { Other, Word, Emoji }
}
