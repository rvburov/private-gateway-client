using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Platform.Windows;

/// <summary>
/// Блок при обрыве на Windows — фильтры Windows Filtering Platform своего провайдера и подслоя на
/// слоях установки соединения (ALE connect и accept, IPv4 и IPv6) и пересылки (IPFORWARD: хот-спот, общий
/// доступ, ВМ и контейнеры с NAT), по правилам <see cref="WfpRules"/>.
/// Сессия не динамическая: фильтры переживают службу (fail-closed) и уходят при снятии, а без «Защиты при
/// загрузке» — и при перезагрузке; с ней фильтры постоянные (<see cref="KillSwitchPolicy.Persistent"/>):
/// служба фильтрации Windows ставит их при загрузке, еще до старта нашей службы. Провайдер и подслой —
/// постоянные всегда (на них ссылаются постоянные фильтры) и снимаются вместе с блоком.
/// Замена — в одной транзакции: прежние фильтры удаляются, новые ставятся, щели нет.
/// Ключи фильтров — по порядку от своего базового GUID: найти и снять их можно и после падения
/// службы. Наш block чужим permit не отменить, наш permit не отменяет чужой block (брандмауэр Windows).
/// Структуры WFP пишутся по смещениям 64-битной Windows (fwpmtypes.h).
/// </summary>
/// <param name="onlyProgram">
/// Только для тестов: фильтры установки соединения касаются одной программы (полный путь), а не всей машины — так
/// правила проверяются на машине CI, не отрезая ее от сети. Фильтры пересылки программы не знают и встают на всю
/// машину: саму машину они не трогают. null — вся машина.
/// </param>
[SupportedOSPlatform("windows")]
public sealed class WfpKillSwitch(string? onlyProgram = null) : IKillSwitch
{
    private const uint RpcAuthnWinNt = 10;
    private const uint AlreadyExists = 0x80320009;
    private const uint FilterNotFound = 0x80320003;

    // Сколько ненайденных ключей подряд — конец наших фильтров (DeleteFilters).
    private const int MissesToEnd = 32;
    private const uint Persistent = 1;
    private const ushort SublayerWeight = 0xFFFF;

    private const uint ActionBlock = 0x1001;
    private const uint ActionPermit = 0x1002;
    private const uint MatchEqual = 0;
    private const uint MatchFlagsAllSet = 6;
    private const uint Uint8 = 1;
    private const uint Uint16 = 2;
    private const uint Uint32 = 3;
    private const uint Uint64 = 4;
    private const uint ByteArray16 = 11;
    private const uint ByteBlob = 12;
    private const uint V4AddrMask = 0x100;
    private const uint V6AddrMask = 0x101;

    private const int FilterSize = 200;
    private const int ConditionSize = 40;

    public static readonly Guid Provider = new("f64ca6d4-a024-4226-9c13-76180c11d297");
    public static readonly Guid Sublayer = new("00a87489-d299-442c-80f2-3f7835431470");

    private static readonly Guid ConnectV4 = new("c38d57d1-05a7-4c33-904f-7fbceee60e82");
    private static readonly Guid AcceptV4 = new("e1cd9fe7-f4b5-4273-96c0-592e487b8650");
    private static readonly Guid ConnectV6 = new("4a72393b-319f-44bc-84c3-ba54dcb3b6b4");
    private static readonly Guid AcceptV6 = new("a3b42c97-9f04-4672-b87e-cee9c483257f");
    private static readonly Guid ForwardV4 = new("a82acc24-4ee1-4ee1-b465-fd1d25cb10a4");
    private static readonly Guid ForwardV6 = new("7b964818-19c7-493a-b71f-832c3684d28c");

    // Раньше словаря полей: статические поля заполняются по порядку текста.
    private static readonly Guid AppId = new("d78e1e87-8644-4ea5-9437-d809ecefc971");

    private static readonly Dictionary<WfpField, Guid> Fields = new()
    {
        [WfpField.Flags] = new("632ce23b-5167-435c-86d7-e903684aa80c"),
        [WfpField.LocalInterface] = new("4cd62a49-59c3-4969-b7f3-bda5d32890a4"),
        [WfpField.RemoteAddress] = new("b235ae9a-1d64-49b8-a44c-5ff3d9095045"),
        [WfpField.RemotePort] = new("c35a604d-d22b-4e1a-91b4-68f674ee674b"),
        [WfpField.LocalPort] = new("0c1ba1af-5765-453f-af22-a8f791ac775b"),
        [WfpField.Protocol] = new("3971ef2b-623e-4f9a-8cb1-6e79b806b9a7"),
        [WfpField.IcmpType] = new("0c1ba1af-5765-453f-af22-a8f791ac775b"),
        [WfpField.App] = AppId,
        [WfpField.DestinationAddress] = new("2d79133b-b390-45c6-8699-acaceaafed33"),
    };

    public string Name => "WFP";

    /// <summary>Что не вышло с брандмауэром Hyper-V при последнем включении или снятии; все вышло — null.</summary>
    public string? Warning { get; private set; }

    // Сколько фильтров поставил последний Enable: сторож блока проверяет, что на месте все.
    private int _filters;

    public bool IsEnabled => FirstFilterFlags() is not null;

    /// <summary>
    /// На месте все фильтры последнего включения, а не только первый: другая программа могла снять часть. Включала не эта
    /// служба (блок с прошлого запуска) — как <see cref="IsEnabled"/>.
    /// </summary>
    public bool IsIntact => Volatile.Read(ref _filters) is var count and > 0
        ? WithEngine(engine => Enumerable.Range(0, count).All(index => FilterExists(engine, index)))
        : IsEnabled;

    /// <summary>Фильтры постоянные — переживут перезагрузку («Защита при загрузке»).</summary>
    public static bool IsPersistent => (FirstFilterFlags() & Persistent) != 0;

    public void Enable(KillSwitchPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ulong? luid = policy.TunnelInterface is { } name && ConvertInterfaceAliasToLuid(name, out var found) == 0 ? found : null;
        var rules = WfpRules.For(policy, luid);
        WithEngine(engine => Transaction(engine, () =>
        {
            DeleteFilters(engine);
            AddBase(engine);
            using var memory = new NativeMemory();
            var app = onlyProgram is null ? IntPtr.Zero : memory.AppId(onlyProgram);
            var index = 0;
            foreach (var rule in rules)
            {
                foreach (var layer in Layers(rule))
                {
                    // У пересылки нет программы: тестовое условие ALE_APP_ID там не ставится. Правило со своей программой (поиск
                    // сервера службой, обход sing-box) и так касается одной программы: второе условие на то же поле WFP
                    // объединил бы через «или» (а то же самое — отверг как повтор).
                    var only = rule.Hook == WfpHook.Forward || rule.Conditions.Any(c => c.Field == WfpField.App) ? IntPtr.Zero : app;
                    Check(FwpmFilterAdd0(engine, Filter(memory, WfpRules.FilterKey(index++), layer, rule, only, policy.Persistent), IntPtr.Zero, out _),
                        "фильтр " + rule.Name);
                }
            }

            Volatile.Write(ref _filters, index);
        }));

        // ВМ (WSL, Hyper-V) — брандмауэром Hyper-V, пока туннеля нет: их трафик через NAT может идти мимо слоев IP. В
        // тестовом режиме машину не трогаем.
        if (onlyProgram is null)
        {
            Warning = luid is null ? HyperVFirewall.Close() : HyperVFirewall.Open();
        }
    }

    /// <summary>Блок службы: правила Hyper-V без фильтров WFP (служба упала, перезагрузка без «Защиты при загрузке») снимаются.</summary>
    public static WfpKillSwitch ForService()
    {
        var killSwitch = new WfpKillSwitch();
        if (!killSwitch.IsEnabled)
        {
            killSwitch.Warning = HyperVFirewall.Open();
        }

        return killSwitch;
    }

    /// <summary>Слои фильтра: установка соединения — исходящие и входящие, пересылка — один слой семейства.</summary>
    private static Guid[] Layers(WfpRule rule) => (rule.Hook, rule.Family) switch
    {
        (WfpHook.Forward, WfpFamily.V4) => [ForwardV4],
        (WfpHook.Forward, _) => [ForwardV6],
        (_, WfpFamily.V4) => [ConnectV4, AcceptV4],
        _ => [ConnectV6, AcceptV6],
    };

    public void Disable()
    {
        WithEngine(engine => Transaction(engine, () =>
        {
            DeleteFilters(engine);
            DeleteBase(engine);
            Volatile.Write(ref _filters, 0);
        }));
        if (onlyProgram is null)
        {
            Warning = HyperVFirewall.Open();
        }
    }

    /// <summary>Флаги первого нашего фильтра; фильтров нет — null.</summary>
    private static uint? FirstFilterFlags() => WithEngine(engine =>
    {
        var key = WfpRules.FilterKey(0);
        if (FwpmFilterGetByKey0(engine, ref key, out var filter) != 0)
        {
            return (uint?)null;
        }

        var flags = (uint)Marshal.ReadInt32(filter, 32);
        FwpmFreeMemory0(ref filter);
        return flags;
    });

    /// <summary>Фильтр с ключом номер <paramref name="index"/> на месте.</summary>
    private static bool FilterExists(IntPtr engine, int index)
    {
        var key = WfpRules.FilterKey(index);
        if (FwpmFilterGetByKey0(engine, ref key, out var filter) != 0)
        {
            return false;
        }

        FwpmFreeMemory0(ref filter);
        return true;
    }

    /// <summary>
    /// Снять наши фильтры: ключи идут подряд от нулевого. Другая программа могла снять часть, и в нумерации дырка — поэтому
    /// конец не первый ненайденный ключ, а <see cref="MissesToEnd"/> ненайденных подряд: иначе оставшиеся после дырки фильтры
    /// не дали бы поставить блок заново (уже есть).
    /// </summary>
    private static void DeleteFilters(IntPtr engine)
    {
        for (int index = 0, misses = 0; index <= ushort.MaxValue && misses < MissesToEnd; index++)
        {
            var key = WfpRules.FilterKey(index);
            var result = FwpmFilterDeleteByKey0(engine, ref key);
            if (result == FilterNotFound)
            {
                misses++;
                continue;
            }

            Check(result, "снятие фильтра");
            misses = 0;
        }
    }

    /// <summary>
    /// Свой провайдер и подслой с наибольшим весом, постоянные: на них ссылаются постоянные фильтры. Прежние
    /// (в том числе непостоянные от прошлой версии) — заново: наших фильтров в эту минуту нет.
    /// </summary>
    private static void AddBase(IntPtr engine)
    {
        DeleteBase(engine);
        using var memory = new NativeMemory();
        var name = memory.String("Private Gateway");
        var provider = memory.Zeroed(64);
        Marshal.StructureToPtr(Provider, provider, false);
        Marshal.WriteIntPtr(provider, 16, name);
        Marshal.WriteInt32(provider, 32, (int)Persistent);
        Ignore(FwpmProviderAdd0(engine, provider, IntPtr.Zero), "провайдер");

        var sublayer = memory.Zeroed(72);
        Marshal.StructureToPtr(Sublayer, sublayer, false);
        Marshal.WriteIntPtr(sublayer, 16, name);
        Marshal.WriteInt32(sublayer, 32, (int)Persistent);
        Marshal.WriteIntPtr(sublayer, 40, memory.Guid(Provider));
        Marshal.WriteInt16(sublayer, 64, unchecked((short)SublayerWeight));
        Ignore(FwpmSubLayerAdd0(engine, sublayer, IntPtr.Zero), "подслой");

        static void Ignore(uint result, string what)
        {
            if (result != AlreadyExists)
            {
                Check(result, what);
            }
        }
    }

    /// <summary>Снять подслой и провайдер; их нет — не ошибка. Фильтры к этому времени сняты.</summary>
    private static void DeleteBase(IntPtr engine)
    {
        var sublayer = Sublayer;
        var provider = Provider;
        _ = FwpmSubLayerDeleteByKey0(engine, ref sublayer);
        _ = FwpmProviderDeleteByKey0(engine, ref provider);
    }

    /// <summary>FWPM_FILTER0 по смещениям 64-битной Windows; <paramref name="persistent"/> — переживает перезагрузку.</summary>
    private static IntPtr Filter(NativeMemory memory, Guid key, Guid layer, WfpRule rule, IntPtr app, bool persistent)
    {
        var conditions = rule.Conditions.Select(c => (Key: Fields[c.Field], Condition: c)).ToList();
        var count = conditions.Count + (app == IntPtr.Zero ? 0 : 1);
        var array = count == 0 ? IntPtr.Zero : memory.Zeroed(ConditionSize * count);
        for (var i = 0; i < conditions.Count; i++)
        {
            Condition(memory, array + (ConditionSize * i), conditions[i].Key, conditions[i].Condition);
        }

        if (app != IntPtr.Zero)
        {
            var at = array + (ConditionSize * conditions.Count);
            Marshal.StructureToPtr(AppId, at, false);
            Marshal.WriteInt32(at, 16, (int)MatchEqual);
            Marshal.WriteInt32(at, 24, (int)ByteBlob);
            Marshal.WriteIntPtr(at, 32, app);
        }

        var filter = memory.Zeroed(FilterSize);
        Marshal.StructureToPtr(key, filter, false);
        Marshal.WriteIntPtr(filter, 16, memory.String("Private Gateway: " + rule.Name));
        Marshal.WriteInt32(filter, 32, persistent ? (int)Persistent : 0);
        Marshal.WriteIntPtr(filter, 40, memory.Guid(Provider));
        Marshal.StructureToPtr(layer, filter + 64, false);
        Marshal.StructureToPtr(Sublayer, filter + 80, false);
        Marshal.WriteInt32(filter, 96, (int)Uint8);
        Marshal.WriteInt64(filter, 104, rule.Weight);
        Marshal.WriteInt32(filter, 112, count);
        Marshal.WriteIntPtr(filter, 120, array);
        Marshal.WriteInt32(filter, 128, (int)(rule.Permit ? ActionPermit : ActionBlock));
        return filter;
    }

    /// <summary>FWPM_FILTER_CONDITION0: поле, сравнение и значение (FWP_CONDITION_VALUE0).</summary>
    private static void Condition(NativeMemory memory, IntPtr at, Guid field, WfpCondition condition)
    {
        Marshal.StructureToPtr(field, at, false);
        Marshal.WriteInt32(at, 16, (int)(condition.Field == WfpField.Flags ? MatchFlagsAllSet : MatchEqual));
        var (type, value) = condition switch
        {
            { Address: { } address, PrefixLength: { } prefix } when address.AddressFamily == AddressFamily.InterNetwork =>
                (V4AddrMask, memory.Bytes([.. Host32(address), .. Host32(new IPAddress(Mask(prefix)))])),
            { Address: { } address, PrefixLength: { } prefix } => (V6AddrMask, memory.Bytes([.. address.GetAddressBytes(), (byte)prefix])),
            { Address: { } address } when address.AddressFamily == AddressFamily.InterNetwork =>
                (Uint32, new IntPtr(BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes()))),
            { Address: { } address } => (ByteArray16, memory.Bytes(address.GetAddressBytes())),
            { Program: { } program } => (ByteBlob, memory.AppId(program)),
            { Field: WfpField.LocalInterface } => (Uint64, memory.Bytes(BitConverter.GetBytes(condition.Number))),
            // Протокол — UINT8, флаги — UINT32, порты и тип ICMP — UINT16; значение — в младших байтах.
            { Field: WfpField.Protocol } => (Uint8, new IntPtr(checked((long)condition.Number))),
            { Field: WfpField.Flags } => (Uint32, new IntPtr(checked((long)condition.Number))),
            _ => (Uint16, new IntPtr(checked((long)condition.Number))),
        };
        Marshal.WriteInt32(at, 24, (int)type);
        Marshal.WriteIntPtr(at, 32, value);

        // Адрес и маска IPv4 в WFP — числа в порядке байтов машины.
        static byte[] Host32(IPAddress address) => BitConverter.GetBytes(BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes()));

        static byte[] Mask(int prefix)
        {
            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            var bytes = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, mask);
            return bytes;
        }
    }

    private static void Transaction(IntPtr engine, Action work)
    {
        Check(FwpmTransactionBegin0(engine, 0), "начало транзакции");
        try
        {
            work();
            Check(FwpmTransactionCommit0(engine), "транзакция");
        }
        catch
        {
            _ = FwpmTransactionAbort0(engine);
            throw;
        }
    }

    private static void WithEngine(Action<IntPtr> work) => WithEngine(engine =>
    {
        work(engine);
        return true;
    });

    private static T WithEngine<T>(Func<IntPtr, T> work)
    {
        Check(FwpmEngineOpen0(IntPtr.Zero, RpcAuthnWinNt, IntPtr.Zero, IntPtr.Zero, out var engine), "открытие WFP");
        try
        {
            return work(engine);
        }
        finally
        {
            _ = FwpmEngineClose0(engine);
        }
    }

    private static void Check(uint result, string what)
    {
        if (result != 0)
        {
            throw new IOException($"WFP: {what} — ошибка 0x{result:X8}");
        }
    }

    /// <summary>Неуправляемая память для одного вызова WFP; освобождается вся сразу.</summary>
    private sealed class NativeMemory : IDisposable
    {
        private readonly List<IntPtr> _blocks = [];
        private readonly List<IntPtr> _appIds = [];

        public IntPtr Zeroed(int size)
        {
            var block = Marshal.AllocHGlobal(size);
            Marshal.Copy(new byte[size], 0, block, size);
            _blocks.Add(block);
            return block;
        }

        public IntPtr Bytes(byte[] bytes)
        {
            var block = Zeroed(bytes.Length);
            Marshal.Copy(bytes, 0, block, bytes.Length);
            return block;
        }

        public IntPtr Guid(Guid value) => Bytes(value.ToByteArray());

        public IntPtr String(string value)
        {
            var block = Marshal.StringToHGlobalUni(value);
            _blocks.Add(block);
            return block;
        }

        /// <summary>FWP_BYTE_BLOB с идентификатором программы для условия ALE_APP_ID.</summary>
        public IntPtr AppId(string program)
        {
            Check(FwpmGetAppIdFromFileName0(program, out var blob), "программа " + program);
            _appIds.Add(blob);
            return blob;
        }

        public void Dispose()
        {
            foreach (var block in _blocks)
            {
                Marshal.FreeHGlobal(block);
            }

            for (var i = 0; i < _appIds.Count; i++)
            {
                var blob = _appIds[i];
                FwpmFreeMemory0(ref blob);
            }
        }
    }

    // Без строк в структурах и без небезопасного кода — LibraryImport не нужен.
#pragma warning disable SYSLIB1054
    [DllImport("fwpuclnt.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint FwpmEngineOpen0(IntPtr serverName, uint authnService, IntPtr authIdentity, IntPtr session, out IntPtr engine);

    [DllImport("fwpuclnt.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint FwpmEngineClose0(IntPtr engine);

    [DllImport("fwpuclnt.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint FwpmTransactionBegin0(IntPtr engine, uint flags);

    [DllImport("fwpuclnt.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint FwpmTransactionCommit0(IntPtr engine);

    [DllImport("fwpuclnt.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint FwpmTransactionAbort0(IntPtr engine);

    [DllImport("fwpuclnt.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint FwpmProviderAdd0(IntPtr engine, IntPtr provider, IntPtr securityDescriptor);

    [DllImport("fwpuclnt.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint FwpmSubLayerAdd0(IntPtr engine, IntPtr sublayer, IntPtr securityDescriptor);

    [DllImport("fwpuclnt.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint FwpmProviderDeleteByKey0(IntPtr engine, ref Guid key);

    [DllImport("fwpuclnt.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint FwpmSubLayerDeleteByKey0(IntPtr engine, ref Guid key);

    [DllImport("fwpuclnt.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint FwpmFilterAdd0(IntPtr engine, IntPtr filter, IntPtr securityDescriptor, out ulong id);

    [DllImport("fwpuclnt.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint FwpmFilterDeleteByKey0(IntPtr engine, ref Guid key);

    [DllImport("fwpuclnt.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint FwpmFilterGetByKey0(IntPtr engine, ref Guid key, out IntPtr filter);

    [DllImport("fwpuclnt.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void FwpmFreeMemory0(ref IntPtr memory);

    [DllImport("fwpuclnt.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint FwpmGetAppIdFromFileName0(string fileName, out IntPtr appId);

    [DllImport("iphlpapi.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint ConvertInterfaceAliasToLuid(string alias, out ulong luid);
#pragma warning restore SYSLIB1054
}
