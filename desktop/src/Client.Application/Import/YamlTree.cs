using System.Text.Json.Nodes;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

namespace PrivateGateway.Client.Application.Import;

/// <summary>
/// YAML (конфиг Clash/Mihomo) — в дерево <see cref="JsonNode"/>: значения — строки, «~», «null» и
/// пустые без кавычек — null. Строится без рекурсии по событиям разборщика, с пределами глубины и
/// числа узлов: подписка приходит из сети, и вложенность или ссылки-«бомбы» не должны уронить
/// программу. Ссылки (*имя) копируют узел, слияние «&lt;&lt;» добавляет ключи, которых нет.
/// </summary>
internal static class YamlTree
{
    private const int MaxDepth = 64;
    private const int MaxNodes = 200_000;

    /// <summary>Корень первого документа; null и номер строки в <paramref name="errorLine"/> — не YAML или вне пределов.</summary>
    /// <param name="text">Текст YAML.</param>
    /// <param name="lines">Сюда — строка начала каждого словаря и списка (с 1).</param>
    /// <param name="errorLine">Строка ошибки (с 1); 0 — разобрано.</param>
    public static JsonNode? Parse(string text, Dictionary<JsonNode, int> lines, out int errorLine)
    {
        errorLine = 0;
        var parser = new Parser(new StringReader(text));
        var anchors = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var stack = new Stack<Frame>();
        JsonNode? root = null;
        var nodes = 0;
        var line = 1;
        try
        {
            while (parser.MoveNext())
            {
                var current = parser.Current!;
                line = (int)current.Start.Line;
                switch (current)
                {
                    case DocumentEnd:
                        return root;
                    case Scalar scalar when stack.TryPeek(out var top) && top.Node is JsonObject && top.Key is null:
                        top.Key = scalar.Value;
                        break;
                    case Scalar scalar:
                        var value = scalar.Style == ScalarStyle.Plain && scalar.Value is "" or "~" or "null" or "Null" or "NULL"
                            ? null
                            : JsonValue.Create(scalar.Value);
                        Count(ref nodes, 1);
                        Remember(anchors, scalar.Anchor, value);
                        Add(stack, ref root, value);
                        break;
                    case AnchorAlias alias:
                        if (!anchors.TryGetValue(alias.Value.Value, out var anchored))
                        {
                            throw new YamlException(alias.Start, alias.End, "Нет якоря для ссылки");
                        }

                        // Сначала счет, потом копия: «бомба» из ссылок останавливается до того, как займет память.
                        Count(ref nodes, Size(anchored));
                        Add(stack, ref root, anchored?.DeepClone());
                        break;
                    case MappingStart or SequenceStart:
                        if (stack.TryPeek(out var parent) && parent.Node is JsonObject && parent.Key is null)
                        {
                            throw new YamlException(current.Start, current.End, "Составной ключ");
                        }

                        if (stack.Count >= MaxDepth)
                        {
                            throw new YamlException(current.Start, current.End, "Слишком глубокая вложенность");
                        }

                        JsonNode container = current is MappingStart ? new JsonObject() : new JsonArray();
                        Count(ref nodes, 1);
                        lines[container] = line;
                        Add(stack, ref root, container);
                        stack.Push(new Frame(container, ((NodeEvent)current).Anchor));
                        break;
                    case MappingEnd or SequenceEnd:
                        var done = stack.Pop();
                        if (done.Node is JsonObject target)
                        {
                            foreach (var merged in done.Merges)
                            {
                                foreach (var (key, item) in merged)
                                {
                                    if (!target.ContainsKey(key))
                                    {
                                        target[key] = item?.DeepClone();
                                    }
                                }
                            }
                        }

                        Remember(anchors, done.Anchor, done.Node);
                        break;
                }
            }

            return root;
        }
        catch (YamlException e)
        {
            errorLine = Math.Max(1, (int)e.Start.Line);
            return null;
        }
        catch (InvalidOperationException)
        {
            // Предел узлов; так же YamlDotNet отвечает на незакрытый «{» или «[» — строка последнего события.
            errorLine = line;
            return null;
        }
    }

    private static void Add(Stack<Frame> stack, ref JsonNode? root, JsonNode? value)
    {
        if (!stack.TryPeek(out var top))
        {
            root = value;
            return;
        }

        if (top.Node is JsonArray array)
        {
            array.Add(value);
            return;
        }

        var key = top.Key!;
        top.Key = null;
        if (key == "<<")
        {
            // Слияние: один словарь или список словарей; явные ключи важнее.
            top.Merges.AddRange(value switch
            {
                JsonObject one => [one],
                JsonArray many => many.OfType<JsonObject>(),
                _ => [],
            });
            return;
        }

        ((JsonObject)top.Node)[key] = value;
    }

    private static void Remember(Dictionary<string, JsonNode?> anchors, AnchorName anchor, JsonNode? node)
    {
        if (!anchor.IsEmpty)
        {
            anchors[anchor.Value] = node;
        }
    }

    private static void Count(ref int nodes, int added)
    {
        nodes += added;
        if (nodes > MaxNodes)
        {
            throw new InvalidOperationException("Слишком много узлов");
        }
    }

    /// <summary>Число узлов копии: глубина копии не больше <see cref="MaxDepth"/>, обход — стеком.</summary>
    private static int Size(JsonNode? node)
    {
        var size = 0;
        var pending = new Stack<JsonNode?>();
        pending.Push(node);
        while (pending.TryPop(out var next))
        {
            size++;
            if (size > MaxNodes)
            {
                break;
            }

            var children = next switch
            {
                JsonObject o => o.Select(p => p.Value),
                JsonArray a => a,
                _ => [],
            };
            foreach (var child in children)
            {
                pending.Push(child);
            }
        }

        return size;
    }

    private sealed class Frame(JsonNode node, AnchorName anchor)
    {
        public JsonNode Node { get; } = node;

        public AnchorName Anchor { get; } = anchor;

        /// <summary>Ключ словаря, значение которого еще не пришло.</summary>
        public string? Key { get; set; }

        public List<JsonObject> Merges { get; } = [];
    }
}
