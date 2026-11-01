using PrivateGateway.Client.Core;

namespace PrivateGateway.Client.Service;

public sealed partial class Gateway
{
    /// <summary>Механизм защиты виртуальных машин; наличие правил не считается проверкой их пакетов.</summary>
    public VirtualMachineProtection VirtualMachines => VirtualMachinesFor(Status);

    private VirtualMachineProtection VirtualMachinesFor(ConnectionStatus status)
    {
        if (!OperatingSystem.IsWindows())
        {
            return VirtualMachineProtection.NotApplicable;
        }

        if (status.Mode != ConnectionMode.Vpn || status.IsPaused || !_blocking || _held is { Open: true })
        {
            return VirtualMachineProtection.Off;
        }

        return _options.KillSwitch?.VirtualMachines ?? VirtualMachineProtection.Unknown;
    }

    private int? ReadyLocalProxyPort(ConnectionStatus status)
    {
        if (status.State != ConnectionState.Connected || status.IsPaused || _request is not { } request)
        {
            return null;
        }

        try
        {
            return status.Mode == ConnectionMode.Vpn
                ? Volatile.Read(ref _tunnel) is { HasExited: false } tunnel ? tunnel.LocalProxyPort : null
                : _core is { HasExited: false } ? request.Connection.ProxyPort : null;
        }
        catch (InvalidOperationException)
        {
            // Остановка могла освободить Process сразу после чтения ссылки. Такой вход не подтверждается.
            return null;
        }
    }
}
