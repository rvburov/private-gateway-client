using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PrivateGateway.Client.Platform.Windows;

/// <summary>
/// Служба Windows через диспетчер служб (advapi32): зарегистрировать, запустить, остановить, удалить.
/// Служба — от LocalSystem, запуск — автоматически с ОС, при сбое диспетчер перезапускает ее через 5 с.
/// Нужны права администратора; ошибка ОС — <see cref="Win32Exception"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsServices
{
    private const uint ScManagerAllAccess = 0xF003F;
    private const uint ServiceAllAccess = 0xF01FF;
    private const uint OwnProcess = 0x10;
    private const uint AutoStart = 2;
    private const uint ErrorNormal = 1;
    private const uint NoChange = 0xFFFFFFFF;
    private const uint ConfigDescription = 1;
    private const uint ConfigFailureActions = 2;
    private const uint ControlStop = 1;
    private const uint Stopped = 1;
    private const int ServiceExists = 1073;
    private const int ServiceDoesNotExist = 1060;
    private const int ServiceNotActive = 1062;
    private const int ServiceAlreadyRunning = 1056;
    private const int CannotAcceptControl = 1061;

    public static bool IsInstalled(string name)
    {
        using var manager = Manager();
        using var service = Open(manager, name, throwIfMissing: false);
        return service is not null;
    }

    /// <summary>Зарегистрировать службу или обновить команду запуска у существующей.</summary>
    /// <param name="name">Имя службы.</param>
    /// <param name="displayName">Название для людей.</param>
    /// <param name="description">Описание в «Службах».</param>
    /// <param name="commandLine">Команда запуска: путь в кавычках и параметры.</param>
    public static void Install(string name, string displayName, string description, string commandLine)
    {
        using var manager = Manager();
        var handle = CreateServiceW(manager, name, displayName, ServiceAllAccess, OwnProcess, AutoStart, ErrorNormal, commandLine,
            null, IntPtr.Zero, null, null, null);
        if (handle == IntPtr.Zero && Marshal.GetLastWin32Error() == ServiceExists)
        {
            using var existing = Open(manager, name, throwIfMissing: true)!;
            Check(ChangeServiceConfigW(existing, OwnProcess, AutoStart, ErrorNormal, commandLine, null, IntPtr.Zero, null, null, null, displayName));
            Configure(existing, description);
            return;
        }

        using var service = new Handle(handle == IntPtr.Zero ? throw new Win32Exception() : handle);
        Configure(service, description);
    }

    public static void Start(string name)
    {
        using var manager = Manager();
        using var service = Open(manager, name, throwIfMissing: true)!;
        if (!StartServiceW(service, 0, IntPtr.Zero) && Marshal.GetLastWin32Error() != ServiceAlreadyRunning)
        {
            throw new Win32Exception();
        }
    }

    /// <summary>
    /// Остановить и дождаться остановки (не дольше <paramref name="timeout"/>); не работает — ничего. Служба, которая
    /// как раз запускается (диспетчер перезапускает упавшую), команду остановки не примет — попытки до срока.
    /// </summary>
    public static void Stop(string name, TimeSpan timeout)
    {
        using var manager = Manager();
        using var service = Open(manager, name, throwIfMissing: false);
        if (service is null)
        {
            return;
        }

        var deadline = DateTime.UtcNow + timeout;
        while (!ControlService(service, ControlStop, out _) && Marshal.GetLastWin32Error() is var error && error != ServiceNotActive)
        {
            if (error != CannotAcceptControl || DateTime.UtcNow >= deadline)
            {
                throw new Win32Exception(error);
            }

            Thread.Sleep(200);
        }

        while (QueryServiceStatus(service, out var status) && status.CurrentState != Stopped && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(200);
        }
    }

    /// <summary>Остановить и удалить; службы нет — ничего.</summary>
    public static void Uninstall(string name, TimeSpan timeout)
    {
        Stop(name, timeout);
        using var manager = Manager();
        using var service = Open(manager, name, throwIfMissing: false);
        if (service is not null)
        {
            Check(DeleteService(service));
        }
    }

    /// <summary>Описание и перезапуск при сбое: три раза через 5 с, счетчик сбрасывается через сутки.</summary>
    private static void Configure(Handle service, string description)
    {
        var text = Marshal.StringToHGlobalUni(description);
        var describe = Marshal.AllocHGlobal(IntPtr.Size);
        var actions = Marshal.AllocHGlobal(8 * 3);
        var failure = Marshal.AllocHGlobal(IntPtr.Size == 8 ? 40 : 20);
        try
        {
            Marshal.WriteIntPtr(describe, text);
            Check(ChangeServiceConfig2W(service, ConfigDescription, describe));

            // SC_ACTION: тип 1 — перезапуск, задержка в мс.
            for (var i = 0; i < 3; i++)
            {
                Marshal.WriteInt32(actions, i * 8, 1);
                Marshal.WriteInt32(actions, (i * 8) + 4, 5000);
            }

            // SERVICE_FAILURE_ACTIONS: сброс счетчика (с), сообщение, команда, число действий, действия.
            Marshal.Copy(new byte[IntPtr.Size == 8 ? 40 : 20], 0, failure, IntPtr.Size == 8 ? 40 : 20);
            Marshal.WriteInt32(failure, 0, 86400);
            Marshal.WriteInt32(failure, IntPtr.Size == 8 ? 24 : 12, 3);
            Marshal.WriteIntPtr(failure, IntPtr.Size == 8 ? 32 : 16, actions);
            Check(ChangeServiceConfig2W(service, ConfigFailureActions, failure));
        }
        finally
        {
            Marshal.FreeHGlobal(text);
            Marshal.FreeHGlobal(describe);
            Marshal.FreeHGlobal(actions);
            Marshal.FreeHGlobal(failure);
        }
    }

    private static Handle Manager() =>
        OpenSCManagerW(null, null, ScManagerAllAccess) is var handle && handle != IntPtr.Zero ? new Handle(handle) : throw new Win32Exception();

    private static Handle? Open(Handle manager, string name, bool throwIfMissing)
    {
        var handle = OpenServiceW(manager, name, ServiceAllAccess);
        if (handle != IntPtr.Zero)
        {
            return new Handle(handle);
        }

        return Marshal.GetLastWin32Error() == ServiceDoesNotExist && !throwIfMissing ? null : throw new Win32Exception();
    }

    private static void Check(bool ok)
    {
        if (!ok)
        {
            throw new Win32Exception();
        }
    }

    /// <summary>Описатель диспетчера служб или службы.</summary>
    private sealed class Handle(IntPtr value) : IDisposable
    {
        public static implicit operator IntPtr(Handle handle) => handle.Value;

        public IntPtr Value { get; } = value;

        public void Dispose() => CloseServiceHandle(Value);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

#pragma warning disable SYSLIB1054
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr CreateServiceW(IntPtr manager, string name, string displayName, uint access, uint serviceType, uint startType,
        uint errorControl, string binaryPath, string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool ChangeServiceConfigW(IntPtr service, uint serviceType, uint startType, uint errorControl, string binaryPath,
        string? loadOrderGroup, IntPtr tagId, string? dependencies, string? account, string? password, string displayName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool ChangeServiceConfig2W(IntPtr service, uint infoLevel, IntPtr info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool StartServiceW(IntPtr service, uint argumentCount, IntPtr arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool ControlService(IntPtr service, uint control, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatus status);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool CloseServiceHandle(IntPtr handle);
#pragma warning restore SYSLIB1054
}
