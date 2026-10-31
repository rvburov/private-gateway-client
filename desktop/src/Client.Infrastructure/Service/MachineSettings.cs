using System.Collections;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Infrastructure.Service;

/// <summary>
/// Настройки компьютера, которые могут мешать (решение 508). Читает окно: переменные окружения прокси (Windows — пользователя и
/// машины из реестра, Linux и macOS — сеанса окна), системный прокси другой программы (<see cref="UserProxySettings"/>), на
/// Windows еще автоопределение прокси (WPAD) и прокси WinHTTP. Исправляет только на Windows и только свое: переменную
/// пользователя и системный прокси пользователя, если его вход не отвечает; прежнее значение — в файл копии в папке
/// программы. Переменные машины и WinHTTP требуют прав администратора — для них экран дает инструкцию.
/// </summary>
public sealed class MachineSettings : IMachineSettings
{
    /// <summary>Переменные окружения прокси, как их понимают curl, git, pip, .NET; регистр на Linux и macOS различается.</summary>
    public static readonly IReadOnlySet<string> ProxyVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "http_proxy", "https_proxy", "all_proxy", "ftp_proxy", "no_proxy",
    };

    private const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string Connections = InternetSettings + @"\Connections";

    private readonly IUserProxySettings _systemProxy;
    private readonly Func<HindranceScope, IReadOnlyDictionary<string, string>> _environment;
    private readonly Func<byte[]?> _winHttp;
    private readonly Func<byte[]?> _connections;
    private readonly Action<Hindrance> _remove;
    private readonly string _backups;
    private readonly TimeProvider _time;
    private readonly bool _canFix;

    /// <param name="systemProxy">Системный прокси пользователя.</param>
    /// <param name="environment">Переменные окружения прокси по области (только имена из <see cref="ProxyVariables"/>).</param>
    /// <param name="winHttp">WinHttpSettings из реестра машины; не Windows — null.</param>
    /// <param name="connections">DefaultConnectionSettings параметров Интернета пользователя; не Windows — null.</param>
    /// <param name="remove">Убрать настройку из ОС.</param>
    /// <param name="backups">Папка копий прежних значений.</param>
    /// <param name="time">Часы — для имени копии.</param>
    /// <param name="canFix">Исправлять умеет только Windows.</param>
    public MachineSettings(IUserProxySettings systemProxy, Func<HindranceScope, IReadOnlyDictionary<string, string>> environment, Func<byte[]?> winHttp,
        Func<byte[]?> connections, Action<Hindrance> remove, string backups, TimeProvider time, bool canFix)
    {
        (_systemProxy, _environment, _winHttp, _connections, _remove, _backups, _time, _canFix) =
            (systemProxy, environment, winHttp, connections, remove, backups, time, canFix);
    }

    /// <summary>Настройки пользователя, от имени которого работает окно; копии — в папке данных программы.</summary>
    public static MachineSettings ForCurrentUser(IUserProxySettings systemProxy, string dataDirectory, TimeProvider time) =>
        OperatingSystem.IsWindows()
            ? ForWindows(systemProxy, Path.Combine(dataDirectory, "backups"), time)
            : new MachineSettings(systemProxy, _ => Variables(Environment.GetEnvironmentVariables()), () => null, () => null, _ => { },
                Path.Combine(dataDirectory, "backups"), time, canFix: false);

    public IReadOnlyList<Hindrance> Read(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var found = new List<Hindrance>();
        foreach (var scope in _canFix ? [HindranceScope.User, HindranceScope.Machine] : new[] { HindranceScope.Session })
        {
            found.AddRange(_environment(scope).Where(v => ProxyVariables.Contains(v.Key) && v.Value.Trim().Length > 0)
                .OrderBy(v => v.Key, StringComparer.Ordinal)
                .Select(v => new Hindrance(HindranceKind.EnvironmentProxy, v.Key, v.Value.Trim(), scope)));
        }

        if (_systemProxy.Read() is { } proxy && proxy.IsForeign(settings))
        {
            found.Add(proxy.IsScript
                ? new Hindrance(HindranceKind.AutoConfig, "AutoConfigURL", proxy.Address, HindranceScope.User)
                : new Hindrance(HindranceKind.SystemProxy, "ProxyServer", proxy.Address, HindranceScope.User));
        }

        if (AutoDetect(_connections()))
        {
            found.Add(new Hindrance(HindranceKind.AutoDetect, "AutoDetect", string.Empty, HindranceScope.User));
        }

        if (WinHttpProxy(_winHttp()) is { } winHttp)
        {
            found.Add(new Hindrance(HindranceKind.WinHttpProxy, "WinHttpSettings", winHttp, HindranceScope.Machine));
        }

        return found;
    }

    public bool CanFix(Hindrance hindrance, EntryCheck? entry)
    {
        ArgumentNullException.ThrowIfNull(hindrance);
        return _canFix && !hindrance.HasCredentials && entry is { Listens: false } && hindrance.Scope == HindranceScope.User
            && hindrance.Kind is HindranceKind.EnvironmentProxy or HindranceKind.SystemProxy or HindranceKind.AutoConfig
            && !hindrance.Name.Equals("no_proxy", StringComparison.OrdinalIgnoreCase);
    }

    public string Fix(Hindrance hindrance)
    {
        ArgumentNullException.ThrowIfNull(hindrance);
        Directory.CreateDirectory(_backups);
        var now = _time.GetLocalNow();
        var path = Path.Combine(_backups, $"settings-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{hindrance.Name}.json");
        var backup = new JsonObject
        {
            ["time"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["kind"] = hindrance.Kind.ToString(),
            ["name"] = hindrance.Name,
            ["scope"] = hindrance.Scope.ToString(),
            ["value"] = hindrance.Value,
        };
        File.WriteAllText(path, backup.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        _remove(hindrance);
        return path;
    }

    /// <summary>
    /// Прокси WinHTTP из WinHttpSettings (как его ставит <c>netsh winhttp set proxy</c>): размер, счетчик, флаги (2 — прокси),
    /// длина и строка прокси (ANSI). Прямое подключение, пусто или не разобрать — null.
    /// </summary>
    public static string? WinHttpProxy(byte[]? blob)
    {
        if (blob is not { Length: >= 16 })
        {
            return null;
        }

        var flags = BitConverter.ToInt32(blob, 8);
        var length = BitConverter.ToInt32(blob, 12);
        return (flags & 2) == 0 || length <= 0 || 16 + length > blob.Length ? null : Encoding.ASCII.GetString(blob, 16, length).Trim();
    }

    /// <summary>Автоопределение прокси (WPAD) из DefaultConnectionSettings: флаг 8 третьего слова.</summary>
    public static bool AutoDetect(byte[]? blob) => blob is { Length: >= 12 } && (BitConverter.ToInt32(blob, 8) & 8) != 0;

    [SupportedOSPlatform("windows")]
    private static MachineSettings ForWindows(IUserProxySettings systemProxy, string backups, TimeProvider time) =>
        new(systemProxy, scope => Variables(Environment.GetEnvironmentVariables(scope == HindranceScope.Machine
                ? EnvironmentVariableTarget.Machine : EnvironmentVariableTarget.User)),
            () => Binary(Registry.LocalMachine, Connections, "WinHttpSettings"), () => Binary(Registry.CurrentUser, Connections, "DefaultConnectionSettings"),
            RemoveOnWindows, backups, time, canFix: true);

    private static Dictionary<string, string> Variables(IDictionary variables) =>
        variables.Cast<DictionaryEntry>()
            .Where(e => e.Key is string name && ProxyVariables.Contains(name) && e.Value is string)
            .ToDictionary(e => (string)e.Key, e => (string)e.Value!, StringComparer.Ordinal);

    [SupportedOSPlatform("windows")]
    private static byte[]? Binary(RegistryKey root, string path, string name)
    {
        using var key = root.OpenSubKey(path);
        return key?.GetValue(name) as byte[];
    }

    /// <summary>Переменная пользователя — из реестра (Windows сама сообщит программам); прокси пользователя — выключить или убрать скрипт.</summary>
    [SupportedOSPlatform("windows")]
    private static void RemoveOnWindows(Hindrance hindrance)
    {
        switch (hindrance.Kind)
        {
            case HindranceKind.EnvironmentProxy:
                Environment.SetEnvironmentVariable(hindrance.Name, null, EnvironmentVariableTarget.User);
                break;
            case HindranceKind.SystemProxy or HindranceKind.AutoConfig:
                using (var key = Registry.CurrentUser.OpenSubKey(InternetSettings, writable: true))
                {
                    if (hindrance.Kind == HindranceKind.SystemProxy)
                    {
                        key?.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                    }
                    else
                    {
                        key?.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
                    }
                }

                // Программы на WinINet перечитывают параметры сразу: INTERNET_OPTION_SETTINGS_CHANGED (39) и REFRESH (37).
                InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
                break;
            default:
                throw new InvalidOperationException("Эту настройку окно не исправляет: " + hindrance.Kind);
        }
    }

    // Без строк — LibraryImport не нужен.
    [DllImport("wininet.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int length);
}
