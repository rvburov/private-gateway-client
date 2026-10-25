using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PrivateGateway.Client.Platform.Windows;

/// <summary>
/// Процессы ядер — в объекте задания Windows с «снять все при закрытии»: служба упала или ее сняли —
/// ОС закрывает задание и снимает ядра. Иначе они пережили бы службу: туннель с адаптером и портами
/// остался бы без хозяина, а новый запуск службы не поднял бы свой. На Linux то же делает systemd
/// (KillMode по умолчанию — весь cgroup службы).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ChildProcesses : IDisposable
{
    private const int ExtendedLimitInformation = 9;
    private const uint KillOnJobClose = 0x2000;

    private static readonly Lazy<ChildProcesses> Shared = new(() => new ChildProcesses());

    private readonly IntPtr _job;

    public ChildProcesses()
    {
        _job = CreateJobObjectW(IntPtr.Zero, null);
        if (_job == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        // JOBOBJECT_EXTENDED_LIMIT_INFORMATION: 144 байта на x64, 112 на x86; LimitFlags — по смещению 16 на обеих.
        var size = IntPtr.Size == 8 ? 144 : 112;
        var info = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, info, size);
            Marshal.WriteInt32(info, 16, (int)KillOnJobClose);
            if (!SetInformationJobObject(_job, ExtendedLimitInformation, info, (uint)size))
            {
                var error = Marshal.GetLastPInvokeError();
                CloseHandle(_job);
                throw new Win32Exception(error);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(info);
        }
    }

    /// <summary>Задание этого процесса: его не закрывают — ОС закрывает его, когда процесс завершился или упал.</summary>
    public static ChildProcesses OfThisProcess => Shared.Value;

    /// <summary>Снять <paramref name="process"/>, когда задание закроется.</summary>
    public void Add(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!AssignProcessToJobObject(_job, process.Handle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>Закрыть задание — и снять все его процессы.</summary>
    public void Dispose() => CloseHandle(_job);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
