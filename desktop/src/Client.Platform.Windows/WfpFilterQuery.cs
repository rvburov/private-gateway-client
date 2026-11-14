namespace PrivateGateway.Client.Platform.Windows;

/// <summary>Данные перечисленного фильтра, нужные для отбора только правил Private Gateway.</summary>
public sealed record WfpFilterEntry(Guid Key, Guid? Provider, Guid Sublayer, uint Flags);

/// <summary>Отсутствие фильтра WFP отличается от отказа или сбоя чтения: ошибка не подтверждает снятие блока.</summary>
public static class WfpFilterQuery
{
    public const uint NotFound = 0x80320003;
    public const uint ProviderNotFound = 0x80320005;
    public const uint SublayerNotFound = 0x80320007;

    public static bool Found(uint result) => result switch
    {
        0 => true,
        NotFound => false,
        _ => throw new IOException($"WFP: чтение фильтра — ошибка 0x{result:X8}"),
    };

    /// <summary>Номера ключей могут иметь любые пробелы; чужой провайдер или подслой никогда не попадает в удаление.</summary>
    public static IReadOnlyList<WfpFilterEntry> Owned(IEnumerable<IReadOnlyList<WfpFilterEntry>> pages, Guid provider, Guid sublayer)
    {
        ArgumentNullException.ThrowIfNull(pages);
        return [.. pages.SelectMany(page => page).Where(entry => entry.Provider == provider && entry.Sublayer == sublayer)];
    }

    public static void RequireRemoved(uint result, uint notFound, string operation)
    {
        if (result != 0 && result != notFound)
        {
            throw new IOException($"WFP: {operation} — ошибка 0x{result:X8}");
        }
    }
}
