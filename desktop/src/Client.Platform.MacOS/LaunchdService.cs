using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using System.Text;

namespace PrivateGateway.Client.Platform.MacOS;

/// <summary>
/// Служба launchd: описание (plist) в /Library/LaunchDaemons и <c>launchctl</c> в системном домене — загрузить с
/// запуском, перезапустить, выгрузить и удалить. Служба — от root, стартует вместе с ОС (RunAtLoad), при сбое
/// launchd перезапускает ее не чаще раза в 5 с (KeepAlive: SuccessfulExit = false, ThrottleInterval).
/// </summary>
/// <param name="launchctl">Вызов launchctl: аргументы → код выхода и текст ошибки.</param>
/// <param name="daemonFolder">Папка описаний служб; тесты дают свою.</param>
/// <param name="forget">Сколько ждать, пока launchd забудет выгруженную службу; не задано — 10 с.</param>
public sealed class LaunchdService(
    Func<IReadOnlyList<string>, (int Code, string Error)> launchctl, string daemonFolder = "/Library/LaunchDaemons", TimeSpan? forget = null)
{
    private const string Program = "/bin/launchctl";

    /// <summary>Ответ bootout «3: No such process» (ESRCH): службы в домене уже нет — выгружать нечего.</summary>
    private const int NoSuchProcess = 3;

    private readonly TimeSpan _forget = forget ?? TimeSpan.FromSeconds(10);

    /// <summary>launchctl этой машины; не macOS — null.</summary>
    public static LaunchdService? ForThisMachine() =>
        OperatingSystem.IsMacOS() && File.Exists(Program) ? new LaunchdService(Run) : null;

    /// <summary>Описание службы: метка, команда — путь и параметры по отдельности (кавычки не нужны), запуск с ОС.</summary>
    public static string Plist(string label, IReadOnlyList<string> command, string? bundleIdentifier = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        var text = new StringBuilder()
            .Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n")
            .Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n")
            .Append("<plist version=\"1.0\">\n<dict>\n")
            .Append("  <key>Label</key>\n  <string>").Append(SecurityElement.Escape(label)).Append("</string>\n");
        if (bundleIdentifier is not null)
        {
            text.Append("  <key>AssociatedBundleIdentifiers</key>\n  <array>\n    <string>")
                .Append(SecurityElement.Escape(bundleIdentifier)).Append("</string>\n  </array>\n");
        }

        text.Append("  <key>ProgramArguments</key>\n  <array>\n");
        foreach (var argument in command)
        {
            text.Append("    <string>").Append(SecurityElement.Escape(argument)).Append("</string>\n");
        }

        return text
            .Append("  </array>\n")
            .Append("  <key>RunAtLoad</key>\n  <true/>\n")
            .Append("  <key>KeepAlive</key>\n  <dict>\n    <key>SuccessfulExit</key>\n    <false/>\n  </dict>\n")
            .Append("  <key>ThrottleInterval</key>\n  <integer>5</integer>\n")
            .Append("</dict>\n</plist>\n")
            .ToString();
    }

    public bool IsInstalled(string label) => File.Exists(PlistPath(label));

    /// <summary>Служба загружена в launchd (работает или ждет перезапуска).</summary>
    public bool IsLoaded(string label) => launchctl(["print", "system/" + label]).Code == 0;

    /// <summary>Записать описание (права 0644); не запускает — это делает <see cref="Start"/>.</summary>
    public void Register(string label, IReadOnlyList<string> command, string? bundleIdentifier = null)
    {
        var path = PlistPath(label);
        File.WriteAllText(path, Plist(label, command, bundleIdentifier));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    /// <summary>Загрузить описание — служба стартует (RunAtLoad); уже загружена — перезапустить с новым описанием.</summary>
    public void Start(string label)
    {
        Unload(label);
        Check(["bootstrap", "system", PlistPath(label)]);
    }

    /// <summary>Выгрузить: launchd останавливает службу (SIGTERM, потом SIGKILL) и ждет ее конца; не загружена — ничего.</summary>
    public void Stop(string label)
    {
        if (IsInstalled(label))
        {
            Unload(label);
        }
    }

    /// <summary>Остановить и удалить описание; его нет — ничего.</summary>
    public void Uninstall(string label)
    {
        if (!IsInstalled(label))
        {
            return;
        }

        Stop(label);
        File.Delete(PlistPath(label));
    }

    private string PlistPath(string label) => Path.Combine(daemonFolder, label + ".plist");

    /// <summary>
    /// Выгрузить и дождаться, пока launchd забудет службу. bootout возвращается, когда процесс остановлен, но запись службы
    /// в домене исчезает чуть позже: <c>print</c> еще находит ее, повторный bootout отвечает «3: No such process» (macOS CI
    /// 2026-11-06 — обновление поверх работающей откатилось), а bootstrap на ней не загрузит новое описание.
    /// </summary>
    private void Unload(string label)
    {
        if (!IsLoaded(label))
        {
            return;
        }

        var (code, error) = launchctl(["bootout", "system/" + label]);
        if (code != 0 && code != NoSuchProcess)
        {
            throw new IOException($"launchctl bootout system/{label}: {code} {error}".Trim());
        }

        var waited = Stopwatch.StartNew();
        while (IsLoaded(label))
        {
            if (waited.Elapsed > _forget)
            {
                throw new IOException($"launchd не выгрузил службу {label} за {_forget.TotalSeconds:0} с");
            }

            Thread.Sleep(100);
        }
    }

    private void Check(IReadOnlyList<string> arguments)
    {
        var (code, error) = launchctl(arguments);
        if (code != 0)
        {
            throw new IOException($"launchctl {string.Join(' ', arguments)}: {code} {error}".Trim());
        }
    }

    private static (int Code, string Error) Run(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(Program) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(start) ?? throw new IOException("launchctl не запустился");
            var error = process.StandardError.ReadToEndAsync();
            _ = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, error.GetAwaiter().GetResult().Trim());
        }
        catch (Win32Exception e)
        {
            throw new IOException("launchctl не запустился: " + e.Message, e);
        }
    }
}
