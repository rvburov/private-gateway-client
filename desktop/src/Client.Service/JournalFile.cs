using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Core;

namespace PrivateGateway.Client.Service;

/// <summary>
/// Журнал службы на диске: после аварии и перезагрузки видно, что было до сбоя и что восстановлено. Строка JSON на запись
/// (время, уровень, источник, текст — уже скрытый журналом службы) в папке состояния: journal.jsonl, дорос до
/// <see cref="MaxSize"/> — становится journal.1.jsonl (прежний уходит), пишется новый. Права — только у владельца (Unix 0600).
/// Не записалось — служба работает дальше: журнал в памяти остается.
/// </summary>
public sealed class JournalFile(string folder) : IDisposable
{
    /// <summary>Размер файла журнала, после которого он сменяется новым.</summary>
    public const long MaxSize = 1 << 20;

    // Русский текст — как есть, а не \uXXXX: файл читается глазами и не растет втрое. В HTML журнал не вставляется.
    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly Lock _gate = new();
    private StreamWriter? _writer;

    /// <summary>Нынешний файл журнала.</summary>
    public string Current => Path.Combine(folder, "journal.jsonl");

    private string Previous => Path.Combine(folder, "journal.1.jsonl");

    public void Append(JournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var line = new JsonObject
        {
            ["time"] = entry.Time.ToString("O", CultureInfo.InvariantCulture),
            ["level"] = entry.Level.ToString(),
            ["source"] = entry.Source,
            ["message"] = entry.Message,
        }.ToJsonString(Readable);
        lock (_gate)
        {
            try
            {
                var writer = _writer ??= Open();
                if (writer.BaseStream.Length >= MaxSize)
                {
                    writer.Dispose();
                    File.Move(Current, Previous, overwrite: true);
                    writer = _writer = Open();
                }

                writer.WriteLine(line);
                writer.Flush();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _writer?.Dispose();
                _writer = null;
            }
        }
    }

    /// <summary>Последние <paramref name="count"/> записей с диска, от старых к новым; битые строки (обрыв на аварии) пропускаются.</summary>
    public IReadOnlyList<JournalEntry> Tail(int count)
    {
        lock (_gate)
        {
            var lines = new List<string>();
            foreach (var path in new[] { Previous, Current })
            {
                try
                {
                    if (File.Exists(path))
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        using var reader = new StreamReader(stream);
                        while (reader.ReadLine() is { } line)
                        {
                            lines.Add(line);
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Не прочитался — без него.
                }
            }

            return [.. lines.Select(Parse).OfType<JournalEntry>().TakeLast(Math.Max(0, count))];
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private StreamWriter Open()
    {
        Directory.CreateDirectory(folder);
        var created = !File.Exists(Current);
        var torn = !created && EndsMidLine();
        var stream = new FileStream(Current, FileMode.Append, FileAccess.Write, FileShare.Read);
        if (created && !OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Current, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var writer = new StreamWriter(stream);
        if (torn)
        {
            // Прошлый запуск оборвался посреди строки: новая запись — с новой строки, а не приклеенной к обрывку.
            writer.WriteLine();
        }

        return writer;
    }

    private bool EndsMidLine()
    {
        using var stream = new FileStream(Current, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0)
        {
            return false;
        }

        stream.Seek(-1, SeekOrigin.End);
        return stream.ReadByte() != '\n';
    }

    private static JournalEntry? Parse(string line)
    {
        try
        {
            return JsonNode.Parse(line) is JsonObject json
                && DateTimeOffset.TryParse(json["time"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var time)
                && Enum.TryParse<JournalLevel>(json["level"]?.GetValue<string>(), out var level)
                && json["source"]?.GetValue<string>() is { } source && json["message"]?.GetValue<string>() is { } message
                ? new JournalEntry(time, level, source, message)
                : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}
