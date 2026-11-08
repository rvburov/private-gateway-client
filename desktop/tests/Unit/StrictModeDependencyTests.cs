using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Tests.Unit;

public sealed class StrictModeDependencyTests
{
    [Fact]
    public void Disabling_kill_switch_clears_strict_in_one_saved_change_and_does_not_restore_it()
    {
        var store = new Store(AppSettings.Default with { Protection = ProtectionSettings.Default with { StrictMode = true } });
        var settings = new SettingsService(store);
        var changes = new List<AppSettings>();
        settings.Changed += (_, value) => changes.Add(value);

        settings.Update(s => s with { KillSwitch = false });

        changes.Count.ShouldBe(1);
        (changes[0].KillSwitch, changes[0].Protection.StrictMode).ShouldBe((false, false));
        new SettingsService(store).Current.Protection.StrictMode.ShouldBeFalse();
        settings.Update(s => s with { KillSwitch = true });
        settings.Current.Protection.StrictMode.ShouldBeFalse();
    }

    [Fact]
    public void Old_disabled_kill_switch_does_not_leave_strict_selected_after_restart_or_import()
    {
        var stale = AppSettings.Default with { KillSwitch = false, Protection = ProtectionSettings.Default with { StrictMode = true } };
        var store = new Store(stale);
        var settings = new SettingsService(store);
        settings.Current.Protection.StrictMode.ShouldBeFalse();
        store.Saved.Protection.StrictMode.ShouldBeFalse();
        settings.Update(_ => stale);
        settings.Current.Protection.StrictMode.ShouldBeFalse();
    }

    [Fact]
    public void Turning_off_strict_keeps_the_ordinary_kill_switch()
    {
        var settings = new SettingsService(new Store(AppSettings.Default with { Protection = ProtectionSettings.Default with { StrictMode = true } }));
        settings.Update(s => s with { Protection = s.Protection with { StrictMode = false } });
        (settings.Current.KillSwitch, settings.Current.Protection.StrictMode).ShouldBe((true, false));
    }

    private sealed class Store(AppSettings value) : ISettingsStore
    {
        public AppSettings Saved { get; private set; } = value;
        public AppSettings Load() => Saved;
        public void Save(AppSettings settings) => Saved = settings;
    }
}
