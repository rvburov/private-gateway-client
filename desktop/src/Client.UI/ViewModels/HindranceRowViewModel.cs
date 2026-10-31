using CommunityToolkit.Mvvm.Input;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.UI.Localization;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>
/// Строка блока «Что на этом компьютере может мешать» (решение 508): что найдено, значение без имени и пароля, работает ли его
/// вход, как убрать; «Исправить» — где окно может исправить само (<see cref="IMachineSettings.CanFix"/>). Цвет чипа — из цветов
/// проверок: не отвечающий прокси — оранжевым (он ломает программы, которые его берут), работающий прокси и другая программа —
/// желтым, не проверенный — акцентом, справка (WPAD, исключения no_proxy) — зеленым: обычно не мешает.
/// </summary>
public sealed partial class HindranceRowViewModel(string title, string detail, string chipText, CheckState state, Hindrance? hindrance = null,
    Func<Hindrance, Task>? fix = null)
{
    public string Title { get; } = title;

    public string Detail { get; } = detail;

    public string ChipText { get; } = chipText;

    public CheckState State { get; } = state;

    public string Icon => CheckIcon.For(State);

    /// <summary>Есть «Исправить».</summary>
    public bool CanFix => hindrance is not null && fix is not null;

    [RelayCommand]
    private Task FixAsync() => CanFix ? fix!(hindrance!) : Task.CompletedTask;

    /// <summary>
    /// Настройка компьютера. <paramref name="entry"/> — что ответила служба о ее входе; <paramref name="checkedEntries"/> — служба
    /// отвечала (без нее вход «не проверен»); <paramref name="canFix"/> — окно может убрать настройку само.
    /// </summary>
    public static HindranceRowViewModel For(Hindrance hindrance, EntryCheck? entry, bool checkedEntries, bool canFix, Func<Hindrance, Task> fix)
    {
        ArgumentNullException.ThrowIfNull(hindrance);
        var loc = Loc.Instance;
        var scope = loc["diag.hinder.scope." + hindrance.Scope.ToString().ToLowerInvariant()];
        var title = hindrance.Kind switch
        {
            HindranceKind.EnvironmentProxy => loc.Format("diag.hinder.env", hindrance.Name, scope),
            HindranceKind.SystemProxy => loc["diag.hinder.system"],
            HindranceKind.AutoConfig => loc["diag.hinder.pac"],
            HindranceKind.AutoDetect => loc["diag.hinder.wpad"],
            _ => loc["diag.hinder.winhttp"],
        };

        if (hindrance.Kind == HindranceKind.AutoDetect)
        {
            return new HindranceRowViewModel(title, loc["diag.hinder.wpad.text"], loc["diag.hinder.chip.on"], CheckState.Ok);
        }

        if (hindrance.Endpoint is null)
        {
            // no_proxy и скрипт без адреса (WPAD в GNOME): проверять нечего.
            return new HindranceRowViewModel(title, loc.Format("diag.hinder.value", hindrance.Shown), loc["diag.hinder.chip.set"], CheckState.Ok);
        }

        var (state, chip, status) = entry switch
        {
            { Listens: true, Process: { } process } => (CheckState.Warning, loc["diag.hinder.chip.live"], loc.Format("diag.hinder.live.process", process)),
            { Listens: true } => (CheckState.Warning, loc["diag.hinder.chip.live"], loc["diag.hinder.live"]),
            { Listens: false } => (CheckState.Stopped, loc["diag.hinder.chip.dead"], loc["diag.hinder.dead"]),
            _ => (CheckState.Waiting, loc["diag.hinder.chip.unknown"], loc[checkedEntries ? "diag.hinder.unknown" : "diag.hinder.noService"]),
        };
        var how = canFix ? null : HowToRemove(hindrance, entry);
        var detail = loc.Format("diag.hinder.detail", hindrance.Shown, status) + (how is null ? string.Empty : " " + how);
        return new HindranceRowViewModel(title, detail, chip, state, canFix ? hindrance : null, canFix ? fix : null);
    }

    /// <summary>Другая программа VPN или прокси, которую нашла служба.</summary>
    public static HindranceRowViewModel For(OtherClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        var loc = Loc.Instance;
        var sign = loc.Format(client.Sign switch
        {
            OtherClientSign.Adapter => "problem.fact.sign.adapter",
            OtherClientSign.Process => "problem.fact.sign.process",
            _ => "problem.fact.sign.proxy",
        }, client.Detail);
        return new HindranceRowViewModel(client.Program.Length > 0 ? client.Program : loc["problem.fact.other_program"],
            loc.Format("diag.hinder.client", sign), loc["diag.hinder.chip.live"], CheckState.Warning);
    }

    /// <summary>Ничего не найдено.</summary>
    public static HindranceRowViewModel Nothing() =>
        new(Loc.Instance["diag.hinder.none"], Loc.Instance["diag.hinder.none.text"], Loc.Instance["diag.hinder.chip.none"], CheckState.Ok);

    /// <summary>Как убрать вручную, если окно само не может: работающий — закрыть программу; иначе — по месту настройки.</summary>
    private static string? HowToRemove(Hindrance hindrance, EntryCheck? entry)
    {
        var loc = Loc.Instance;
        if (entry is { Listens: true })
        {
            return loc["diag.hinder.how.live"];
        }

        return hindrance switch
        {
            { Kind: HindranceKind.WinHttpProxy } => loc["diag.hinder.how.winhttp"],
            { Kind: HindranceKind.EnvironmentProxy, Scope: HindranceScope.Machine } => loc["diag.hinder.how.machine"],
            { Kind: HindranceKind.EnvironmentProxy, Scope: HindranceScope.Session } => loc.Format("diag.hinder.how.session", hindrance.Name),
            { HasCredentials: true } or { Kind: HindranceKind.EnvironmentProxy } => loc["diag.hinder.how.user"],
            _ => loc["diag.hinder.how.system"],
        };
    }
}
