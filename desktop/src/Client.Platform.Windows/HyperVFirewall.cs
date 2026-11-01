using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PrivateGateway.Client.Platform.Windows;

/// <summary>
/// Брандмауэр Hyper-V (Windows 11 22H2 и новее): два правила «закрыть все» — исходящие и входящие — для
/// виртуальных машин WSL и других на коммутаторах Hyper-V. Их трафик через NAT может идти мимо слоев IP WFP,
/// поэтому, пока туннеля нет, его закрывают здесь. Правила — классом WMI <c>MSFT_NetFirewallHyperVRule</c>
/// (тот же набор, что <c>Get-NetFirewallHyperVRule</c>) в постоянном хранилище политики, со своими InstanceID и
/// названиями: их можно найти и снять и после падения службы. Правила переживают перезагрузку.
/// </summary>
[SupportedOSPlatform("windows")]
public static class HyperVFirewall
{
    /// <summary>Что сказать, если брандмауэра Hyper-V нет.</summary>
    public const string Missing = "ВМ WSL и Hyper-V без туннеля могут выйти мимо блока: нет брандмауэра Hyper-V (нужна Windows 11 22H2 и новее)";

    /// <summary>Начало названия наших правил — по нему они находятся.</summary>
    public const string NamePrefix = "Private Gateway: ";

    public const string Namespace = @"root\StandardCimv2";
    public const string Class = "MSFT_NetFirewallHyperVRule";

    /// <summary>Объекта нет (WBEM_E_NOT_FOUND).</summary>
    private const int NotFound = unchecked((int)0x80041002);

    /// <summary>Класса нет (WBEM_E_INVALID_CLASS).</summary>
    private const int InvalidClass = unchecked((int)0x80041010);

    /// <summary>Пространства имен нет (WBEM_E_INVALID_NAMESPACE).</summary>
    private const int InvalidNamespace = unchecked((int)0x8004100E);

    private static int _created;

    /// <summary>Сколько правил поставлено с запуска процесса — по нему видно, что повторная постановка стоящие не трогает.</summary>
    public static int Created => Volatile.Read(ref _created);

    /// <summary>Правила: InstanceID, название, направление (1 — входящие, 2 — исходящие).</summary>
    private static readonly (string Id, string Name, int Direction)[] Rules =
    [
        (HyperVFirewallPolicy.OutboundId, NamePrefix + "ВМ без туннеля закрыты (исходящие)", 2),
        (HyperVFirewallPolicy.InboundId, NamePrefix + "ВМ без туннеля закрыты (входящие)", 1),
    ];

    /// <summary>
    /// Поставить оба правила. Уже стоят оба — не трогать: снять и поставить заново значило бы открыть ВМ на это время при каждой
    /// замене блока. Исправное направление сохраняется при починке другого; старые лишние свои правила снимаются после
    /// установки новой пары. Не вышло — текст для журнала, вышло — null.
    /// </summary>
    public static string? Close() => Try(scope =>
    {
        var ours = Ours(scope);
        try
        {
            var owned = ours.Select(instance => (Instance: instance, State: Snapshot(instance))).ToArray();
            var snapshots = owned.Select(rule => rule.State).ToArray();
            if (HyperVFirewallPolicy.IsClosed(snapshots))
            {
                return;
            }

            using var rule = new ManagementClass(scope, new ManagementPath(Class), new ObjectGetOptions(Store()));
            foreach (var (id, name, direction) in Rules)
            {
                if (HyperVFirewallPolicy.CanKeep(id, snapshots))
                {
                    continue;
                }

                foreach (var bad in owned.Where(o => string.Equals(o.State.Id, id, StringComparison.OrdinalIgnoreCase)))
                {
                    Delete(bad.Instance);
                }

                using var instance = rule.CreateInstance();
                instance["InstanceID"] = id;
                instance["ElementName"] = name;
                instance["Action"] = HyperVFirewallPolicy.Block;
                instance["Enabled"] = HyperVFirewallPolicy.Enabled;
                instance["Direction"] = direction;
                instance.Put(new PutOptions(Store(), TimeSpan.FromSeconds(10), false, PutType.CreateOnly));
                Interlocked.Increment(ref _created);
            }

            foreach (var old in owned.Where(o => !Rules.Any(r => string.Equals(o.State.Id, r.Id, StringComparison.OrdinalIgnoreCase))))
            {
                Delete(old.Instance);
            }
        }
        finally
        {
            foreach (var found in ours)
            {
                found.Dispose();
            }
        }
    });

    /// <summary>Снять наши правила; их нет — не ошибка. Не вышло — текст для журнала, вышло — null.</summary>
    public static string? Open() => Try(Remove);

    /// <summary>Стоят ли наши правила (оба); брандмауэра Hyper-V нет — false.</summary>
    public static bool IsClosed()
    {
        var closed = false;
        return Try(scope =>
        {
            var ours = Ours(scope);
            try
            {
                closed = HyperVFirewallPolicy.IsClosed(ours.Select(Snapshot).ToArray());
            }
            finally
            {
                foreach (var rule in ours)
                {
                    rule.Dispose();
                }
            }
        }) is null && closed;
    }

    /// <summary>Есть ли брандмауэр Hyper-V (класс WMI).</summary>
    public static bool Available() => Try(scope =>
    {
        using var rule = new ManagementClass(scope, new ManagementPath(Class), null);
        rule.Get();
    }) is null;

    /// <summary>Постоянное хранилище политики (как у <c>New-NetFirewallHyperVRule</c>) — для всех операций одно.</summary>
    public static ManagementNamedValueCollection Store() => new() { { "PolicyStore", "PersistentStore" } };

    private static HyperVFirewallRule Snapshot(ManagementObject rule) => new(
        rule["InstanceID"] as string ?? string.Empty,
        Convert.ToInt32(rule["Action"], System.Globalization.CultureInfo.InvariantCulture),
        Convert.ToInt32(rule["Enabled"], System.Globalization.CultureInfo.InvariantCulture),
        Convert.ToInt32(rule["Direction"], System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// Наши правила: по InstanceID или по началу названия. Отбор — здесь, а не условием WQL: условие по этим свойствам
    /// поставщик правил Hyper-V не выполняет (CI, Windows Server 2025), а правил в классе единицы.
    /// </summary>
    private static List<ManagementObject> Ours(ManagementScope scope)
    {
        using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery("SELECT * FROM " + Class),
            new System.Management.EnumerationOptions
            {
                Context = Store(), ReturnImmediately = false, Rewindable = false, Timeout = TimeSpan.FromSeconds(10),
            });
        var ours = new List<ManagementObject>();
        using var result = searcher.Get();
        foreach (var rule in result.Cast<ManagementObject>())
        {
            if (Rules.Any(r => string.Equals(rule["InstanceID"] as string, r.Id, StringComparison.OrdinalIgnoreCase))
                || (rule["ElementName"] as string)?.StartsWith(NamePrefix, StringComparison.Ordinal) == true)
            {
                ours.Add(rule);
            }
            else
            {
                rule.Dispose();
            }
        }

        return ours;
    }

    private static void Remove(ManagementScope scope)
    {
        foreach (var instance in Ours(scope))
        {
            using (instance)
            {
                Delete(instance);
            }
        }
    }

    private static void Delete(ManagementObject instance)
    {
        try
        {
            instance.Delete(new DeleteOptions(Store(), TimeSpan.FromSeconds(10)));
        }
        catch (ManagementException e) when ((int)e.ErrorCode == NotFound)
        {
            // Уже снято.
        }
    }

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
