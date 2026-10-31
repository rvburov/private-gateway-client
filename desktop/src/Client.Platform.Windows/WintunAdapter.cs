using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace PrivateGateway.Client.Platform.Windows;

/// <summary>
/// Адаптер Wintun, который создает sing-box для TUN (решение 510). GUID у него постоянный — sing-tun берет его из имени
/// адаптера: MD5 от «wintun» и имени (<see cref="AdapterGuid"/>), а устройство — <c>SWD\Wintun\{GUID}</c>. Если прежний sing-box
/// был снят и не закрыл адаптер, устройство с этим GUID остается, и на части машин новый адаптер не поднимается: Wintun
/// ждет 15 с и отвечает «Cannot create a file when that file already exists», sing-box выходит. Сам Wintun в этом случае
/// убирает устройство — и вторая попытка проходит; служба делает то же заранее (<see cref="RemoveLeftover"/>), до запуска
/// sing-box, так же, как Wintun: DIF_REMOVE через SetupAPI.
/// </summary>
public static class WintunAdapter
{
    private const uint DiocInheritClassDrivers = 2;
    private const uint DifRemove = 5;
    private const uint DiRemoveDeviceGlobal = 1;
    private const int ErrorNoSuchDevinst = unchecked((int)0xE000020B);

    /// <summary>GUID адаптера с именем <paramref name="name"/>, как его берет sing-tun (generateGUIDByDeviceName).</summary>
    public static Guid AdapterGuid(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        // Адаптер Windows — не данные для защиты: MD5 здесь только повторяет, как sing-tun выводит GUID из имени.
#pragma warning disable CA5351
        return new Guid(MD5.HashData(Encoding.UTF8.GetBytes("wintun" + name)));
#pragma warning restore CA5351
    }

    /// <summary>Код устройства адаптера в PnP: <c>SWD\Wintun\{GUID}</c>.</summary>
    public static string InstanceId(string name) => @"SWD\Wintun\" + AdapterGuid(name).ToString("B").ToUpperInvariant();

    /// <summary>
    /// Убрать устройство адаптера <paramref name="name"/>, оставшееся от прежнего запуска, — в любом состоянии: работающий
    /// sing-box этой службы его не держит (вызывать до запуска). Убрано — true, его нет — false; не далось — исключение.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool RemoveLeftover(string name)
    {
        var set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == new IntPtr(-1))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetupDiCreateDeviceInfoList");
        }

        var id = Marshal.StringToHGlobalUni(InstanceId(name));
        try
        {
            var device = new DeviceInfoData { Size = (uint)Marshal.SizeOf<DeviceInfoData>() };
            if (!SetupDiOpenDeviceInfoW(set, id, IntPtr.Zero, DiocInheritClassDrivers, ref device))
            {
                var error = Marshal.GetLastPInvokeError();
                return error == ErrorNoSuchDevinst ? false : throw new Win32Exception(error, "SetupDiOpenDeviceInfo");
            }

            var parameters = new RemoveDeviceParams
            {
                HeaderSize = (uint)Marshal.SizeOf<uint>() * 2,
                InstallFunction = DifRemove,
                Scope = DiRemoveDeviceGlobal,
            };
            if (!SetupDiSetClassInstallParamsW(set, ref device, ref parameters, (uint)Marshal.SizeOf<RemoveDeviceParams>())
                || !SetupDiCallClassInstaller(DifRemove, set, ref device))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "DIF_REMOVE");
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(id);
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    /// <summary>SP_DEVINFO_DATA (setupapi.h): на 64-битной Windows — 32 байта, выравнивание естественное.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    /// <summary>SP_REMOVEDEVICE_PARAMS: заголовок SP_CLASSINSTALL_HEADER (размер заголовка, функция), область, профиль.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RemoveDeviceParams
    {
        public uint HeaderSize;
        public uint InstallFunction;
        public uint Scope;
        public uint HardwareProfile;
    }

    // Строка — указателем, структуры — без ссылочных полей: LibraryImport не нужен.
    [DllImport("setupapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr parent);

    [DllImport("setupapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiOpenDeviceInfoW(IntPtr set, IntPtr instanceId, IntPtr parent, uint flags, ref DeviceInfoData device);

    [DllImport("setupapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiSetClassInstallParamsW(IntPtr set, ref DeviceInfoData device, ref RemoveDeviceParams parameters, uint size);

    [DllImport("setupapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiCallClassInstaller(uint function, IntPtr set, ref DeviceInfoData device);

    [DllImport("setupapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
}
