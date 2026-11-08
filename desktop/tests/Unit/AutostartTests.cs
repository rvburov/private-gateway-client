using System.Xml.Linq;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Application.Startup;
using PrivateGateway.Client.Core.Platform;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Startup;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Запуск вместе с ОС: записи автозагрузки и когда программа их трогает.</summary>
public sealed class AutostartTests : IDisposable
{
    private const string App = "/opt/Private Gateway/PrivateGateway";
    private readonly string _home = Path.Combine(Path.GetTempPath(), "pg-home-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_home))
        {
            Directory.Delete(_home, recursive: true);
        }
    }

    [Fact]
    public void Linux_gets_an_xdg_autostart_entry_that_starts_in_the_tray()
    {
        var autostart = new OsAutostart(App, AutostartKind.XdgDesktop, _home);

        autostart.Apply(true);

        autostart.EntryPath.ShouldBe(Path.Combine(_home, ".config", "autostart", "private-gateway.desktop"));
        var entry = File.ReadAllText(autostart.EntryPath);
        entry.ShouldStartWith("[Desktop Entry]");
        entry.ShouldContain("Exec=\"/opt/Private Gateway/PrivateGateway\" --minimized");
        autostart.Apply(false);
        File.Exists(autostart.EntryPath).ShouldBeFalse();
        Should.NotThrow(() => autostart.Apply(false), "убрать уже убранное — не ошибка");
    }

    [Fact]
    public void Xdg_config_home_and_special_characters_are_respected()
    {
        var autostart = new OsAutostart("/home/user/$apps/\"pg\"", AutostartKind.XdgDesktop, _home, Path.Combine(_home, "cfg"));

        autostart.Apply(true);

        autostart.EntryPath.ShouldBe(Path.Combine(_home, "cfg", "autostart", "private-gateway.desktop"));
        File.ReadAllText(autostart.EntryPath).ShouldContain("Exec=\"/home/user/\\$apps/\\\"pg\\\"\" --minimized");
    }

    [Fact]
    public void Mac_gets_a_launch_agent_with_the_path_escaped()
    {
        var autostart = new OsAutostart("/Applications/A & B.app/Contents/MacOS/PrivateGateway", AutostartKind.LaunchAgent, _home);

        autostart.Apply(true);

        autostart.EntryPath.ShouldBe(Path.Combine(_home, "Library", "LaunchAgents", "com.privategateway.client.plist"));
        var plist = XDocument.Parse(File.ReadAllText(autostart.EntryPath));
        plist.Descendants("string").Select(e => e.Value).ShouldBe(
            ["com.privategateway.client", MacAppIdentity.BundleIdentifier, "/Applications/A & B.app/Contents/MacOS/PrivateGateway", "--minimized"]);
        plist.Descendants("true").ShouldHaveSingleItem();
    }

    [Fact]
    public void Mac_refresh_adds_bundle_attribution_without_enabling_or_rewriting_the_job()
    {
        var autostart = new OsAutostart(App, AutostartKind.LaunchAgent, _home);
        autostart.Apply(true);
        var document = XDocument.Load(autostart.EntryPath);
        var dictionary = document.Root!.Element("dict")!;
        var association = dictionary.Elements("key").Single(k => k.Value == "AssociatedBundleIdentifiers");
        association.ElementsAfterSelf().First().Remove();
        association.Remove();
        dictionary.Elements("true").Single().ReplaceWith(new XElement("false"));
        dictionary.Add(new XElement("key", "Disabled"), new XElement("true"));
        dictionary.Add(new XElement("key", "ThrottleInterval"), new XElement("integer", 42));
        document.Save(autostart.EntryPath);

        autostart.RefreshMetadata();

        var actual = XDocument.Load(autostart.EntryPath).Root!.Element("dict")!;
        XElement Value(string name) => actual.Elements("key").Single(k => k.Value == name).ElementsAfterSelf().First();
        Value("AssociatedBundleIdentifiers").Element("string")!.Value.ShouldBe(MacAppIdentity.BundleIdentifier);
        Value("ProgramArguments").Elements("string").Select(e => e.Value).ShouldBe([App, "--minimized"]);
        Value("Disabled").Name.LocalName.ShouldBe("true");
        Value("RunAtLoad").Name.LocalName.ShouldBe("false");
        Value("ThrottleInterval").Value.ShouldBe("42");
        var once = File.ReadAllText(autostart.EntryPath);
        autostart.RefreshMetadata();
        File.ReadAllText(autostart.EntryPath).ShouldBe(once, "уже обновленная запись больше не перезаписывается");
        Directory.GetFiles(Path.GetDirectoryName(autostart.EntryPath)!).ShouldHaveSingleItem();
    }

    [Fact]
    public void Mac_refresh_never_restores_a_removed_login_item()
    {
        var autostart = new OsAutostart(App, AutostartKind.LaunchAgent, _home);
        autostart.RefreshMetadata();
        File.Exists(autostart.EntryPath).ShouldBeFalse();
        autostart.Apply(true);
        autostart.Apply(false);
        autostart.RefreshMetadata();
        File.Exists(autostart.EntryPath).ShouldBeFalse();
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("label")]
    [InlineData("path")]
    [InlineData("association")]
    public void Mac_refresh_preserves_unrecognized_or_explicit_registration(string change)
    {
        var autostart = new OsAutostart(App, AutostartKind.LaunchAgent, _home);
        autostart.Apply(true);
        var original = File.ReadAllText(autostart.EntryPath);
        var supplied = change switch
        {
            "broken" => "<plist><dict>",
            "label" => original.Replace(OsAutostart.Identifier, "io.example.other", StringComparison.Ordinal),
            "path" => original.Replace(App, "/Applications/Other.app/Contents/MacOS/Other", StringComparison.Ordinal),
            _ => original.Replace(MacAppIdentity.BundleIdentifier, "io.example.other", StringComparison.Ordinal),
        };
        if (change is "label" or "path")
        {
            var document = XDocument.Parse(supplied);
            var key = document.Descendants("key").Single(k => k.Value == "AssociatedBundleIdentifiers");
            key.ElementsAfterSelf().First().Remove();
            key.Remove();
            supplied = document.ToString();
        }

        File.WriteAllText(autostart.EntryPath, supplied);

        Should.NotThrow(autostart.RefreshMetadata);

        File.ReadAllText(autostart.EntryPath).ShouldBe(supplied);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/usr/share/dotnet/dotnet")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    public void Running_from_sources_is_not_written_anywhere(string? executable)
    {
        var autostart = new OsAutostart(executable, AutostartKind.XdgDesktop, _home);

        autostart.IsSupported.ShouldBeFalse();
        autostart.Apply(true);
        File.Exists(autostart.EntryPath).ShouldBeFalse();
    }

    [Fact]
    public void Windows_registry_entry_is_written_and_removed()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("реестр есть только на Windows");
            return;
        }

        var name = "Private Gateway test " + Guid.NewGuid().ToString("N");
        var autostart = new OsAutostart(@"C:\Program Files\Private Gateway\PrivateGateway.exe", AutostartKind.Registry, _home, entryName: name);
        try
        {
            autostart.Apply(true);
            autostart.RegistryValue().ShouldBe("\"C:\\Program Files\\Private Gateway\\PrivateGateway.exe\" --minimized");
            autostart.Apply(false);
            autostart.RegistryValue().ShouldBeNull();
        }
        finally
        {
            autostart.Apply(false);
        }
    }

    [Fact]
    public void Registry_is_not_supported_outside_windows()
    {
        new OsAutostart(@"C:\PG\PrivateGateway.exe", AutostartKind.Registry, _home).IsSupported.ShouldBe(OperatingSystem.IsWindows());
    }

    [Fact]
    public void Autostart_follows_only_explicit_decisions()
    {
        var settings = new SettingsService(new MemorySettings());
        var autostart = new RecordingAutostart();
        var sync = new AutostartSync(settings, autostart);
        settings.Update(s => s with { Language = "en" });
        sync.Start();
        autostart.MetadataRefreshes.ShouldBe(1);
        autostart.Applied.ShouldBeEmpty("старт обновляет только оформление существующей записи");

        settings.Update(s => s with { KillSwitch = false });
        autostart.Applied.ShouldBeEmpty("другие настройки автозагрузку не трогают");

        settings.Update(s => s with { OnboardingDone = true });
        autostart.Applied.ShouldBe([true], "после первого запуска — по настройке, по умолчанию включено");

        settings.Update(s => s with { StartWithSystem = false });
        settings.Update(s => s with { Language = "ru" });
        settings.Update(s => s with { StartWithSystem = true });
        autostart.Applied.ShouldBe([true, false, true]);

        autostart.Supported = false;
        settings.Update(s => s with { StartWithSystem = false });
        autostart.Applied.Count.ShouldBe(3, "из исходников не пишется");
    }

    private sealed class RecordingAutostart : IAutostart
    {
        public List<bool> Applied { get; } = [];

        public int MetadataRefreshes { get; private set; }

        public void RefreshMetadata() => MetadataRefreshes++;

        public bool Supported { get; set; } = true;

        public bool IsSupported => Supported;

        public void Apply(bool enabled) => Applied.Add(enabled);
    }

    private sealed class MemorySettings : ISettingsStore
    {
        private AppSettings _saved = AppSettings.Default;

        public AppSettings Load() => _saved;

        public void Save(AppSettings settings) => _saved = settings;
    }
}
