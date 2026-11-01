namespace PrivateGateway.Client.Core;

/// <summary>
/// Состояние механизма защиты виртуальных машин. Наличие правил не является проверкой пакетов из каждой ВМ:
/// внешний коммутатор Hyper-V, Windows Sandbox и контейнеры WinNAT требуют отдельного стенда.
/// </summary>
public sealed record VirtualMachineProtection(CheckState State, string Reason)
{
    public static VirtualMachineProtection Unknown { get; } = new(CheckState.Waiting, "not_checked");
    public static VirtualMachineProtection Off { get; } = new(CheckState.Off, "off");
    public static VirtualMachineProtection NotApplicable { get; } = new(CheckState.Off, "not_applicable");

    /// <summary>Обходы виртуальной сети не подтверждаются одной успешной установкой правил ОС.</summary>
    public static VirtualMachineProtection AfterFirewall(string? warning, bool withoutTunnel, bool rulesIntact) =>
        warning is not null ? new(CheckState.Warning, warning == "hyper_v_unavailable" ? "unavailable" : "firewall_error")
        : withoutTunnel ? new(CheckState.Warning, rulesIntact ? "rules_applied" : "rules_missing")
        : new(CheckState.Warning, "tunnel_unverified");
}
