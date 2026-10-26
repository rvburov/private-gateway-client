package com.privategateway.client.core.links

import org.snakeyaml.engine.v2.api.LoadSettings
import org.snakeyaml.engine.v2.common.Anchor
import org.snakeyaml.engine.v2.common.ScalarStyle
import org.snakeyaml.engine.v2.events.AliasEvent
import org.snakeyaml.engine.v2.events.DocumentEndEvent
import org.snakeyaml.engine.v2.events.Event
import org.snakeyaml.engine.v2.events.MappingEndEvent
import org.snakeyaml.engine.v2.events.MappingStartEvent
import org.snakeyaml.engine.v2.events.NodeEvent
import org.snakeyaml.engine.v2.events.ScalarEvent
import org.snakeyaml.engine.v2.events.SequenceEndEvent
import org.snakeyaml.engine.v2.events.SequenceStartEvent
import org.snakeyaml.engine.v2.exceptions.MarkedYamlEngineException
import org.snakeyaml.engine.v2.exceptions.YamlEngineException
import org.snakeyaml.engine.v2.parser.ParserImpl
import org.snakeyaml.engine.v2.scanner.StreamReader
import java.util.IdentityHashMap

/**
 * YAML (конфиг Clash/Mihomo) — в дерево, как у [JsonTree]: значения — строки, «~», «null» и пустые без кавычек — null.
 * Строится без рекурсии по событиям разборщика, с пределами глубины и числа узлов — как на десктопе (`YamlTree`):
 * подписка приходит из сети, и вложенность или ссылки-«бомбы» не должны уронить программу. Ссылки (*имя) копируют
 * узел, слияние «<<» добавляет ключи, которых нет.
 */
internal object YamlTree {
    private const val MAX_DEPTH = 64
    private const val MAX_NODES = 200_000

    /** Ошибка разбора: строка (с 1). */
    private class Broken(val line: Int, message: String) : Exception(message)

    /** Корень первого документа, строки начала каждого словаря и списка (с 1) и строка ошибки (0 — разобрано). */
    class Parsed(val root: Any?, val lines: IdentityHashMap<Any, Int>, val errorLine: Int)

    fun parse(text: String): Parsed {
        val lines = IdentityHashMap<Any, Int>()
        val settings = LoadSettings.builder().build()
        val parser = ParserImpl(settings, StreamReader(settings, text))
        val anchors = HashMap<String, Any?>()
        val stack = ArrayDeque<Frame>()
        var root: Any? = null
        var nodes = 0
        var line = 1
        try {
            while (parser.hasNext()) {
                val current: Event = parser.next()
                line = current.startMark.map { it.line + 1 }.orElse(line)
                val top = stack.lastOrNull()
                when (current) {
                    is DocumentEndEvent -> return Parsed(root, lines, 0)
                    is ScalarEvent -> {
                        if (top != null && top.node is MutableMap<*, *> && top.key == null) {
                            top.key = current.value
                        } else {
                            val value = if (current.scalarStyle == ScalarStyle.PLAIN && current.value in setOf("", "~", "null", "Null", "NULL")) {
                                null
                            } else {
                                current.value
                            }

                            nodes = count(nodes, 1, line)
                            remember(anchors, current.anchor.orElse(null), value)
                            root = add(stack, root, value)
                        }
                    }

                    is AliasEvent -> {
                        val name = current.alias.value
                        if (!anchors.containsKey(name)) {
                            throw Broken(line, "Нет якоря для ссылки")
                        }

                        // Сначала счет, потом копия: «бомба» из ссылок останавливается до того, как займет память.
                        val anchored = anchors[name]
                        nodes = count(nodes, size(anchored), line)
                        root = add(stack, root, copy(anchored))
                    }

                    is MappingStartEvent, is SequenceStartEvent -> {
                        if (top != null && top.node is MutableMap<*, *> && top.key == null) {
                            throw Broken(line, "Составной ключ")
                        }

                        if (stack.size >= MAX_DEPTH) {
                            throw Broken(line, "Слишком глубокая вложенность")
                        }

                        val container: Any = if (current is MappingStartEvent) LinkedHashMap<String, Any?>() else ArrayList<Any?>()
                        nodes = count(nodes, 1, line)
                        lines[container] = line
                        root = add(stack, root, container)
                        stack.addLast(Frame(container, (current as NodeEvent).anchor.orElse(null)))
                    }

                    is MappingEndEvent, is SequenceEndEvent -> {
                        val done = stack.removeLast()
                        @Suppress("UNCHECKED_CAST")
                        val target = done.node as? MutableMap<String, Any?>
                        if (target != null) {
                            for (merged in done.merges) {
                                for ((key, item) in merged) {
                                    if (key is String && !target.containsKey(key)) {
                                        target[key] = copy(item)
                                    }
                                }
                            }
                        }

                        remember(anchors, done.anchor, done.node)
                    }

                    else -> Unit
                }
            }

            return Parsed(root, lines, 0)
        } catch (e: Broken) {
            return Parsed(null, lines, maxOf(1, e.line))
        } catch (e: MarkedYamlEngineException) {
            // Незакрытый «{» или «[» — строка, где конструкция началась (как у десктопа); иначе — где нашлась ошибка.
            val mark = if (e.context?.startsWith("while parsing a flow") == true) e.contextMark.or { e.problemMark } else e.problemMark.or { e.contextMark }
            return Parsed(null, lines, mark.map { it.line + 1 }.orElse(line))
        } catch (_: YamlEngineException) {
            return Parsed(null, lines, line)
        }
    }

    @Suppress("UNCHECKED_CAST")
    private fun add(stack: ArrayDeque<Frame>, root: Any?, value: Any?): Any? {
        val top = stack.lastOrNull() ?: return value
        if (top.node is MutableList<*>) {
            (top.node as MutableList<Any?>).add(value)
            return root
        }

        val key = top.key!!
        top.key = null
        if (key == "<<") {
            // Слияние: один словарь или список словарей; явные ключи важнее.
            when (value) {
                is Map<*, *> -> top.merges.add(value)
                is List<*> -> top.merges.addAll(value.filterIsInstance<Map<*, *>>())
            }

            return root
        }

        (top.node as MutableMap<String, Any?>)[key] = value
        return root
    }

    private fun remember(anchors: HashMap<String, Any?>, anchor: Anchor?, node: Any?) {
        if (anchor != null) {
            anchors[anchor.value] = node
        }
    }

    private fun count(nodes: Int, added: Int, line: Int): Int {
        val total = nodes + added
        if (total > MAX_NODES) {
            throw Broken(line, "Слишком много узлов")
        }

        return total
    }

    /** Число узлов копии: глубина копии не больше [MAX_DEPTH], обход — стеком. */
    private fun size(node: Any?): Int {
        var size = 0
        val pending = ArrayDeque<Any?>()
        pending.addLast(node)
        while (pending.isNotEmpty()) {
            val next = pending.removeLast()
            size++
            if (size > MAX_NODES) {
                break
            }

            when (next) {
                is Map<*, *> -> next.values.forEach { pending.addLast(it) }
                is List<*> -> next.forEach { pending.addLast(it) }
            }
        }

        return size
    }

    /** Глубокая копия узла: словари и списки — новые, строки — те же. */
    private fun copy(node: Any?): Any? = when (node) {
        is Map<*, *> -> LinkedHashMap<String, Any?>().also { copy -> node.forEach { (k, v) -> copy[k as String] = copy(v) } }
        is List<*> -> node.mapTo(ArrayList()) { copy(it) }
        else -> node
    }

    private class Frame(val node: Any, val anchor: Anchor?) {
        /** Ключ словаря, значение которого еще не пришло. */
        var key: String? = null

        val merges = ArrayList<Map<*, *>>()
    }
}
