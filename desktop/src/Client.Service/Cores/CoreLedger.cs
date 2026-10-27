using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PrivateGateway.Client.Service.Cores;

/// <summary>
/// Какие ядра служба запустила и они еще работают — файл в папке состояния службы. Служба пользователя на
/// Linux и macOS не в cgroup systemd и не в задании Windows: убили ее (SIGKILL, сбой) — ядро остается, держит
/// порт прокси и ходит через сервер. Следующий запуск службы снимает такие ядра (<see cref="KillLeftovers"/>).
/// Процесс узнается по номеру, времени запуска и пути программы: номер мог достаться чужому процессу.
/// </summary>
public sealed class CoreLedger(string path)
{
    /// <summary>Насколько может разойтись время запуска одного процесса при двух прочтениях.</summary>
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();

    /// <summary>Файл в папке состояния службы.</summary>
    public static string In(string folder) => Path.Combine(folder, "cores.json");

    /// <summary>Ядро запущено: запомнить его номер, время запуска и настоящий путь программы.</summary>
    public void Add(Process process, string executable)
    {
        ArgumentNullException.ThrowIfNull(process);
        DateTime started;
        try
        {
            started = process.StartTime.ToUniversalTime();
        }
        catch (InvalidOperationException)
        {
            // Ядро уже вышло — помнить нечего.
            return;
        }

        lock (_gate)
        {
            Write([.. Read().Where(e => e.Pid != process.Id), new Entry(process.Id, started, VpnTunnel.RealPath(executable))]);
        }
    }

    /// <summary>Ядро вышло — забыть.</summary>
    public void Remove(int pid)
    {
        lock (_gate)
        {
            var entries = Read();
            if (entries.Any(e => e.Pid == pid))
            {
                Write([.. entries.Where(e => e.Pid != pid)]);
            }
        }
    }

    /// <summary>
    /// Снять ядра прошлого запуска, которые пережили службу; файл после этого пуст. Возвращает номера снятых.
    /// </summary>
    public IReadOnlyList<int> KillLeftovers()
    {
        lock (_gate)
        {
            var killed = new List<int>();
            foreach (var entry in Read())
            {
                if (Find(entry) is not { } process)
                {
                    continue;
                }

                using (process)
                {
                    try
                    {
                        process.Kill(entireProcessTree: true);
                        killed.Add(entry.Pid);
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // Уже выходит само: на Windows ядра прошлой службы снимает ее объект задания, когда она умерла, —
                        // не сразу, и Kill на выходящий процесс отказывает.
                    }

                    // Дождаться выхода в обоих случаях: иначе новое ядро встает, пока прежнее еще держит порт прокси.
                    try
                    {
                        process.WaitForExit(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                        // Ждать нечего — процесса уже нет.
                    }
                }
            }

            Write([]);
            return killed;
        }
    }

    /// <summary>Процесс записи, если это все еще то самое ядро; иначе null.</summary>
    private static Process? Find(Entry entry)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(entry.Pid);
        }
        catch (ArgumentException)
        {
            return null;
        }

        try
        {
            if (!process.HasExited
                && (process.StartTime.ToUniversalTime() - entry.Started).Duration() <= StartTolerance
                && process.MainModule?.FileName is { } file
                && string.Equals(VpnTunnel.RealPath(file), entry.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return process;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Вышел, пока смотрели, или чужой — не трогать.
        }

        process.Dispose();
        return null;
    }

    private List<Entry> Read()
    {
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonArray json)
            {
                return [];
            }

            return [.. json.Select(e => new Entry(
                e!["pid"]!.GetValue<int>(),
                DateTime.Parse(e["started"]!.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                e["path"]!.GetValue<string>()))];
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or NullReferenceException or IOException)
        {
            return [];
        }
    }

    /// <summary>Запись — через временный файл: оборванная запись не портит прежний файл.</summary>
    private void Write(IReadOnlyList<Entry> entries)
    {
        try
        {
            var json = new JsonArray([.. entries.Select(e => (JsonNode)new JsonObject
            {
                ["pid"] = e.Pid,
                ["started"] = e.Started.ToString("O", CultureInfo.InvariantCulture),
                ["path"] = e.Path,
            })]);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, json.ToJsonString());
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Не записалось — ядро работает, только без этой страховки.
        }
    }

    private sealed record Entry(int Pid, DateTime Started, string Path);
}
