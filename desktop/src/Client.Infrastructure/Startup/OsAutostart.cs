using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using PrivateGateway.Client.Application.Startup;

namespace PrivateGateway.Client.Infrastructure.Startup;

/// <summary>Куда ОС смотрит при входе пользователя.</summary>
public enum AutostartKind
{
    /// <summary>Windows: HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
    Registry,

    /// <summary>macOS: ~/Library/LaunchAgents/*.plist с RunAtLoad.</summary>
    LaunchAgent,

    /// <summary>Linux: ~/.config/autostart/*.desktop (XDG Autostart).</summary>
    XdgDesktop,
}

/// <summary>
/// Автозагрузка текущего пользователя — без прав администратора и без службы. Программа запускается
/// с флагом <c>--minimized</c>: окно не показывается, только значок в трее.
/// </summary>
public sealed class OsAutostart : IAutostart
{
    public const string Name = "Private Gateway";
    public const string Identifier = "com.privategateway.client";
    public const string MinimizedArgument = "--minimized";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly string? _executable;
    private readonly AutostartKind _kind;
    private readonly string _home;
    private readonly string? _xdgConfig;
    private readonly string _entryName;

    /// <param name="executable">Путь к программе; null или dotnet — запуск из исходников, писать нечего.</param>
    /// <param name="kind">Куда писать.</param>
    /// <param name="home">Домашняя папка пользователя (в тестах — временная).</param>
    /// <param name="xdgConfig">XDG_CONFIG_HOME, если задан.</param>
    /// <param name="entryName">Имя записи в реестре; в тестах — свое, чтобы не тронуть настоящую.</param>
    public OsAutostart(string? executable, AutostartKind kind, string home, string? xdgConfig = null, string entryName = Name)
    {
        _executable = executable;
        _kind = kind;
        _home = home;
        _xdgConfig = xdgConfig;
        _entryName = entryName;
    }

    /// <summary>Для текущей ОС и текущего процесса.</summary>
    public static OsAutostart ForCurrentUser() => new(
        Environment.ProcessPath,
        OperatingSystem.IsWindows() ? AutostartKind.Registry : OperatingSystem.IsMacOS() ? AutostartKind.LaunchAgent : AutostartKind.XdgDesktop,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : null);

    public bool IsSupported =>
        _executable is { Length: > 0 } executable
        // Имя файла — после любого разделителя: путь мог прийти с другой ОС.
        && Path.GetFileNameWithoutExtension(executable[(executable.LastIndexOfAny(['/', '\\']) + 1)..]) is not ("dotnet" or "testhost")
        && (_kind != AutostartKind.Registry || OperatingSystem.IsWindows());

    /// <summary>Файл автозагрузки для macOS и Linux.</summary>
    public string EntryPath => _kind switch
    {
        AutostartKind.LaunchAgent => Path.Combine(_home, "Library", "LaunchAgents", Identifier + ".plist"),
        _ => Path.Combine(_xdgConfig ?? Path.Combine(_home, ".config"), "autostart", "private-gateway.desktop"),
    };

    public void Apply(bool enabled)
    {
        if (!IsSupported)
        {
            return;
        }

        if (_kind == AutostartKind.Registry)
        {
            if (OperatingSystem.IsWindows())
            {
                ApplyRegistry(enabled);
            }

            return;
        }

        if (!enabled)
        {
            File.Delete(EntryPath);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(EntryPath)!);
        File.WriteAllText(EntryPath, _kind == AutostartKind.LaunchAgent ? LaunchAgent(_executable!) : DesktopEntry(_executable!));
    }

    /// <summary>LaunchAgent: запустить при входе, путь — экранированный для XML.</summary>
    public static string LaunchAgent(string executable) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>Label</key>
          <string>{Identifier}</string>
          <key>ProgramArguments</key>
          <array>
            <string>{SecurityElement.Escape(executable)}</string>
            <string>{MinimizedArgument}</string>
          </array>
          <key>RunAtLoad</key>
          <true/>
        </dict>
        </plist>

        """;

    /// <summary>Запись XDG Autostart; путь в Exec — в кавычках, со спецсимволами по спецификации.</summary>
    public static string DesktopEntry(string executable)
    {
        ArgumentNullException.ThrowIfNull(executable);
        var quoted = "\"" + string.Concat(executable.Select(c => c is '"' or '`' or '$' or '\\' ? "\\" + c : c.ToString())) + "\"";
        return $"""
            [Desktop Entry]
            Type=Application
            Name={Name}
            Exec={quoted} {MinimizedArgument}
            Terminal=false
            X-GNOME-Autostart-enabled=true

            """;
    }

    /// <summary>Значение записи в реестре; null — нет. Только Windows.</summary>
    [SupportedOSPlatform("windows")]
    public string? RegistryValue()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        return run?.GetValue(_entryName) as string;
    }

    [SupportedOSPlatform("windows")]
    private void ApplyRegistry(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
        {
            run.SetValue(_entryName, $"\"{_executable}\" {MinimizedArgument}");
        }
        else
        {
            run.DeleteValue(_entryName, throwOnMissingValue: false);
        }
    }
}
