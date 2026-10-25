using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Platform.Windows;

/// <summary>
/// Системный прокси Windows — «Параметры Интернета» пользователя (HKCU, WinINet): им следуют Edge,
/// Chrome и большинство программ. Локальные адреса и частные сети — напрямую; скрипт автонастройки на
/// время подключения убирается, иначе он важнее ручного прокси. После записи — уведомление WinINet,
/// чтобы программы перечитали настройки сразу.
/// </summary>
/// <param name="keyPath">Раздел в HKCU; тесты пишут в свой, а не в настоящий.</param>
/// <param name="notify">Сообщить программам о смене настроек.</param>
[SupportedOSPlatform("windows")]
public sealed class WindowsSystemProxy(string keyPath = WindowsSystemProxy.InternetSettings, bool notify = true) : ISystemProxy
{
    public const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private const string Enabled = "ProxyEnable";
    private const int SettingsChanged = 39;
    private const int Refresh = 37;

    private static readonly string[] Names = [Enabled, "ProxyServer", "ProxyOverride", "AutoConfigURL"];

    /// <summary>Мимо прокси: локальные имена, 127.*, частные сети 10/8, 172.16/12, 192.168/16.</summary>
    public static string Bypass { get; } =
        string.Join(';', ["<local>", "localhost", "127.*", "10.*", .. Enumerable.Range(16, 16).Select(n => $"172.{n}.*"), "192.168.*"]);

    public string Name => "параметры Интернета Windows";

    public SystemProxySnapshot Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return new SystemProxySnapshot(Names.ToDictionary(n => n, n => key?.GetValue(n) is { } value ? Convert.ToString(value, CultureInfo.InvariantCulture) : null));
    }

    public void Enable(int port)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue("ProxyServer", "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture));
            key.SetValue("ProxyOverride", Bypass);
            key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
            key.SetValue(Enabled, 1, RegistryValueKind.DWord);
        }

        Notify();
    }

    public void Restore(SystemProxySnapshot previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            foreach (var name in Names)
            {
                if (previous.Values.GetValueOrDefault(name) is not { } value)
                {
                    key.DeleteValue(name, throwOnMissingValue: false);
                }
                else if (name == Enabled)
                {
                    key.SetValue(name, int.Parse(value, CultureInfo.InvariantCulture), RegistryValueKind.DWord);
                }
                else
                {
                    key.SetValue(name, value);
                }
            }
        }

        Notify();
    }

    private void Notify()
    {
        if (notify)
        {
            InternetSetOption(IntPtr.Zero, SettingsChanged, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, Refresh, IntPtr.Zero, 0);
        }
    }

    // Без указателей и строк — LibraryImport (и небезопасный код) не нужен.
    [DllImport("wininet.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
#pragma warning disable SYSLIB1054
    private static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int length);
#pragma warning restore SYSLIB1054
}
