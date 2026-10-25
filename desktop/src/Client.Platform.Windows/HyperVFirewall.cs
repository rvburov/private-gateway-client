using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PrivateGateway.Client.Platform.Windows;

/// <summary>
/// Брандмауэр Hyper-V (Windows 11 22H2 и новее): два правила «закрыть все» — исходящие и входящие — для
/// виртуальных машин WSL и других на коммутаторах Hyper-V. Их трафик через NAT может идти мимо слоев IP WFP,
/// поэтому, пока туннеля нет, его закрывают здесь. Правила — классом WMI <c>MSFT_NetFirewallHyperVRule</c>
/// (тот же набор, что <c>Get-NetFirewallHyperVRule</c>), со своими постоянными InstanceID: их можно найти и
/// снять и после падения службы. Правила хранятся постоянно и переживают перезагрузку.
/// </summary>
[SupportedOSPlatform("windows")]
public static class HyperVFirewall
{
    /// <summary>Что сказать, если брандмауэра Hyper-V нет.</summary>
    public const string Missing = "ВМ WSL и Hyper-V без туннеля могут выйти мимо блока: нет брандмауэра Hyper-V (нужна Windows 11 22H2 и новее)";

    private const string Namespace = @"root\StandardCimv2";
    private const string Class = "MSFT_NetFirewallHyperVRule";
    private const int Block = 4;
    private const int Enabled = 1;

    /// <summary>Объекта нет (WBEM_E_NOT_FOUND).</summary>
    private const int NotFound = unchecked((int)0x80041002);

    /// <summary>Класса нет (WBEM_E_INVALID_CLASS).</summary>
    private const int InvalidClass = unchecked((int)0x80041010);

    /// <summary>Пространства имен нет (WBEM_E_INVALID_NAMESPACE).</summary>
    private const int InvalidNamespace = unchecked((int)0x8004100E);

    /// <summary>Правила: InstanceID, название, направление (1 — входящие, 2 — исходящие).</summary>
    private static readonly (string Id, string Name, int Direction)[] Rules =
    [
        ("{6a0f5b8e-3c1d-4e7a-9b2f-0d4c8e1a7f53}", "Private Gateway: ВМ без туннеля закрыты (исходящие)", 2),
        ("{c4e29d71-58b3-4a06-8f1e-2b7d9c3a6e14}", "Private Gateway: ВМ без туннеля закрыты (входящие)", 1),
    ];

    /// <summary>Поставить оба правила (уже стоят — заменить). Не вышло — текст для журнала, вышло — null.</summary>
    public static string? Close() => Try(scope =>
    {
        using var rule = new ManagementClass(scope, new ManagementPath(Class), null);
        foreach (var (id, name, direction) in Rules)
        {
            using var instance = rule.CreateInstance();
            instance["InstanceID"] = id;
            instance["ElementName"] = name;
            instance["Action"] = Block;
            instance["Enabled"] = Enabled;
            instance["Direction"] = direction;
            instance.Put();
        }
    });

    /// <summary>Снять оба правила; их нет — не ошибка. Не вышло — текст для журнала, вышло — null.</summary>
    public static string? Open() => Try(scope =>
    {
        foreach (var (id, _, _) in Rules)
        {
            using var instance = new ManagementObject(scope, new ManagementPath($"{Class}.InstanceID=\"{id}\""), null);
            try
            {
                instance.Delete();
            }
            catch (ManagementException e) when ((int)e.ErrorCode == NotFound)
            {
                // Уже снято.
            }
        }
    });

    /// <summary>Стоят ли наши правила (оба); брандмауэра Hyper-V нет — false.</summary>
    public static bool IsClosed()
    {
        var found = 0;
        var error = Try(scope =>
        {
            foreach (var (id, _, _) in Rules)
            {
                using var instance = new ManagementObject(scope, new ManagementPath($"{Class}.InstanceID=\"{id}\""), null);
                try
                {
                    instance.Get();
                    found++;
                }
                catch (ManagementException e) when ((int)e.ErrorCode == NotFound)
                {
                    // Правила нет.
                }
            }
        });
        return error is null && found == Rules.Length;
    }

    /// <summary>Есть ли брандмауэр Hyper-V (класс WMI).</summary>
    public static bool Available() => Try(scope =>
    {
        using var rule = new ManagementClass(scope, new ManagementPath(Class), null);
        rule.Get();
    }) is null;

    private static string? Try(Action<ManagementScope> work)
    {
        try
        {
            var scope = new ManagementScope(Namespace, new ConnectionOptions { Timeout = TimeSpan.FromSeconds(10) });
            scope.Connect();
            work(scope);
            return null;
        }
        catch (ManagementException e) when ((int)e.ErrorCode is InvalidClass or NotFound or InvalidNamespace)
        {
            return Missing;
        }
        catch (Exception e) when (e is ManagementException or COMException or UnauthorizedAccessException or TimeoutException)
        {
            return "Брандмауэр Hyper-V: " + e.Message;
        }
    }
}
