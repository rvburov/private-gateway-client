using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Infrastructure.Service;

/// <summary>
/// WinINet текущего пользователя окна: чтение и запись четырех параметров одного LAN-подключения через один API.
/// Смешивать запись HKCU и настройки подключения WinINet нельзя: снимок и readback должны описывать одно состояние.
/// </summary>
public sealed class CurrentUserWindowsProxy : ISystemProxy
{
    private const int Flags = 1;
    private const int FlagsUi = 10;
    private const int Server = 2;
    private const int BypassList = 3;
    private const int AutoConfig = 4;
    private const int PerConnection = 75;
    private static readonly int OptionSize = Marshal.SizeOf<ConnectionOption>();
    private static readonly string Bypass = string.Join(';', ["<local>", "localhost", "127.*", "10.*",
        .. Enumerable.Range(16, 16).Select(n => $"172.{n}.*"), "192.168.*"]);

    public string Name => "системный прокси пользователя Windows";

    public static SystemProxySnapshot ForPort(int port) => new(new Dictionary<string, string?>
    {
        ["ProxyEnable"] = "1",
        ["ProxyServer"] = "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture),
        ["ProxyOverride"] = Bypass,
        ["AutoConfigURL"] = null,
        ["ConnectionFlags"] = "3", // PROXY_TYPE_DIRECT | PROXY_TYPE_PROXY: PAC и WPAD на время подключения выключены.
    });

    [SupportedOSPlatform("windows")]
    public SystemProxySnapshot Read()
    {
        var memory = Marshal.AllocHGlobal(OptionSize * 4);
        var queryStarted = false;
        try
        {
            // FLAGS_UI читает выбор пользователя, а не результат последнего обнаружения WPAD.
            var options = new[] { FlagsUi, Server, BypassList, AutoConfig };
            for (var index = 0; index < options.Length; index++)
            {
                Marshal.StructureToPtr(new ConnectionOption { Option = options[index] }, At(memory, index), fDeleteOld: false);
            }

            var list = List(memory);
            var size = list.Size;
            queryStarted = true;
            if (!InternetQueryOption(IntPtr.Zero, PerConnection, ref list, ref size))
            {
                throw new IOException("Windows не подтвердила чтение параметров системного прокси");
            }

            var flags = Marshal.PtrToStructure<ConnectionOption>(memory).Value.Number;
            return new SystemProxySnapshot(new Dictionary<string, string?>
            {
                ["ProxyEnable"] = (flags & 2) != 0 ? "1" : "0",
                ["ProxyServer"] = Text(memory, 1),
                ["ProxyOverride"] = Text(memory, 2),
                ["AutoConfigURL"] = Text(memory, 3),
                ["ConnectionFlags"] = flags.ToString(CultureInfo.InvariantCulture),
            });
        }
        finally
        {
            // Строки Query выделяет WinINet: их освобождает GlobalFree, не allocator вызывающего кода.
            for (var index = 1; queryStarted && index < 4; index++)
            {
                var pointer = Marshal.PtrToStructure<ConnectionOption>(At(memory, index)).Value.Text;
                if (pointer != IntPtr.Zero)
                {
                    _ = GlobalFree(pointer);
                }
            }

            Marshal.FreeHGlobal(memory);
        }
    }

    [SupportedOSPlatform("windows")]
    public void Enable(int port) => Restore(ForPort(port));

    public SystemProxySnapshot? Expected(int port) => ForPort(port);

    [SupportedOSPlatform("windows")]
    public void Restore(SystemProxySnapshot previous)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (!int.TryParse(previous.Values.GetValueOrDefault("ConnectionFlags"), NumberStyles.None,
                CultureInfo.InvariantCulture, out var flags) || flags is < 0 or > 15)
        {
            throw new IOException("Некорректные параметры автонастройки прокси в снимке");
        }

        var strings = new List<IntPtr>(3);
        var memory = Marshal.AllocHGlobal(OptionSize * 4);
        try
        {
            // При записи используется FLAGS; все строки и флаги задаются в одном представлении WinINet.
            Marshal.StructureToPtr(new ConnectionOption { Option = Flags, Value = new OptionValue { Number = flags } }, memory, fDeleteOld: false);
            var fields = new[] { (Server, "ProxyServer"), (BypassList, "ProxyOverride"), (AutoConfig, "AutoConfigURL") };
            for (var index = 0; index < fields.Length; index++)
            {
                var (option, name) = fields[index];
                var value = Marshal.StringToHGlobalUni(previous.Values.GetValueOrDefault(name) ?? string.Empty);
                strings.Add(value);
                Marshal.StructureToPtr(new ConnectionOption { Option = option, Value = new OptionValue { Text = value } },
                    At(memory, index + 1), fDeleteOld: false);
            }

            var list = List(memory);
            if (!InternetSetConnectionOption(IntPtr.Zero, PerConnection, ref list, list.Size))
            {
                throw new IOException("Windows не подтвердила запись параметров системного прокси");
            }
        }
        finally
        {
            // Строки Set выделены нами; WinINet их не получает во владение.
            foreach (var value in strings)
            {
                Marshal.FreeHGlobal(value);
            }

            Marshal.FreeHGlobal(memory);
        }

        var changed = InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
        var refreshed = InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
        if (!changed || !refreshed)
        {
            throw new IOException("Windows не подтвердила уведомление об изменении системного прокси");
        }
    }

    private static IntPtr At(IntPtr memory, int index) => IntPtr.Add(memory, OptionSize * index);

    private static string? Text(IntPtr memory, int index)
    {
        var value = Marshal.PtrToStringUni(Marshal.PtrToStructure<ConnectionOption>(At(memory, index)).Value.Text);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static ConnectionOptionList List(IntPtr memory) => new()
    {
        Size = Marshal.SizeOf<ConnectionOptionList>(), Count = 4, Options = memory,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct ConnectionOptionList
    {
        public int Size;
        public IntPtr Connection;
        public int Count;
        public int Error;
        public IntPtr Options;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConnectionOption
    {
        public int Option;
        public OptionValue Value;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct OptionValue
    {
        [FieldOffset(0)] public int Number;
        [FieldOffset(0)] public IntPtr Text;
        [FieldOffset(0)] public System.Runtime.InteropServices.ComTypes.FILETIME Time;
    }

    [DllImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
#pragma warning disable SYSLIB1054
    private static extern bool InternetQueryOption(IntPtr internet, int option, ref ConnectionOptionList buffer, ref int length);

    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool InternetSetConnectionOption(IntPtr internet, int option, ref ConnectionOptionList buffer, int length);

    [DllImport("wininet.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool InternetSetOption(IntPtr internet, int option, IntPtr buffer, int length);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GlobalFree(IntPtr memory);
#pragma warning restore SYSLIB1054
}
