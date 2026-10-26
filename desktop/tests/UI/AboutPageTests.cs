using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Core.Updates;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;
using PrivateGateway.Client.UI.Views;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>«О программе» (доска 15); снимок — для сверки с docs/design/screens/15-about.png.</summary>
public sealed class AboutPageTests : WindowHarness
{
    [AvaloniaFact]
    public void Shows_the_version_honest_component_states_and_privacy()
    {
        var (window, shell, _) = OpenAbout();

        shell.IsSettings.ShouldBeTrue();
        Text(window, "VersionLine").ShouldBe($"Версия {AppInfo.Version} · {AppInfo.OsVersion} · {AppInfo.Architecture}");
        // На Linux — название дистрибутива: «Ubuntu 24.04 LTS».
        AppInfo.OsVersion.ShouldStartWith(OperatingSystem.IsLinux() ? string.Empty : AppInfo.OsName);
        AppInfo.OsVersion.ShouldNotBeNullOrWhiteSpace();
        Text(window, "ClientVersion").ShouldBe(AppInfo.Version);
        Text(window, "ServiceState").ShouldBe("имитация, демо-режим");
        Text(window, "AboutXray").ShouldBe("не установлено");
        Text(window, "AboutSecondCore").ShouldBe("не выбрано");
        Text(window, "TunDriver").ShouldBe(OperatingSystem.IsWindows() ? "не установлен" : "встроен в ОС");
        Text(window, "ChannelText").ShouldBe("Стабильный");
        Text(window, "UpdatesText").ShouldBe("В демо-режиме обновления не проверяются.", "имитация в сеть не ходит — так и сказано");
        Find<Button>(window, "CheckUpdatesNow").IsEffectivelyVisible.ShouldBeFalse();
        Snapshot(window, "about");
    }

    [AvaloniaFact]
    public void Signed_update_shows_its_highlights_opens_the_release_and_waits_for_later()
    {
        var notes = new Uri("https://github.com/rvburov/private-gateway-client/releases/tag/v0.2.0");
        var (window, page, service) = OpenAboutWith(new UpdateOffer(UpdateState.Available, Time.GetUtcNow(), "0.2.0",
            ["Быстрее переподключение после сна", "Новые правила маршрутизации по сайтам", "Исправления в проверке утечек DNS"], notes));

        service.UpdateChecks.ShouldBe([(UpdateChannel.Stable, false)], "открыли «О программе» — спросили службу, ее память не обходя");
        Text(window, "UpdatesTitle").ShouldBe("Доступно обновление 0.2.0");
        page.UpdateHighlights.Count.ShouldBe(3);
        Find<TextBlock>(window, "UpdateSigned").IsEffectivelyVisible.ShouldBeTrue("подпись манифеста проверила служба");
        Find<TextBlock>(window, "UpdatesText").IsEffectivelyVisible.ShouldBeFalse();
        Snapshot(window, "about-update");

        Find<Button>(window, "UpdateNow").Content.ShouldBe("Открыть выпуск", "служба сама его не поставит — программа не из установщика");
        Press(window, "UpdateNow");
        Dispatcher.UIThread.RunJobs();
        Urls.Opened.ShouldBe([notes]);

        Press(window, "PostponeUpdate");
        Dispatcher.UIThread.RunJobs();
        Text(window, "UpdatesTitle").ShouldBe("Обновления");
        Text(window, "UpdatesText").ShouldBe("Доступна версия 0.2.0", "«Позже» сворачивает, но не прячет");
        Press(window, "ShowUpdate");
        Dispatcher.UIThread.RunJobs();
        page.IsUpdateAvailable.ShouldBeTrue();
    }

    [AvaloniaFact]
    public void Update_now_downloads_with_progress_and_restarts_the_window_after_the_installer()
    {
        var gate = new TaskCompletionSource();
        var (window, page, service) = OpenAboutWith(new UpdateOffer(UpdateState.Available, Time.GetUtcNow(), "0.2.0", ["Быстрее переподключение после сна"],
            new Uri("https://github.com/rvburov/private-gateway-client/releases/tag/v0.2.0"))
        { Installable = true });
        service.Download = [new UpdateProgress(0, 30_093_312), new UpdateProgress(19_293_798, 30_093_312), new UpdateProgress(30_093_312, 30_093_312, Ready: true)];
        service.DownloadGate = gate.Task;
        Find<Button>(window, "UpdateNow").Content.ShouldBe("Обновить сейчас");

        Press(window, "UpdateNow");
        WaitFor(() => page.UpdateFraction > 0.6);

        Text(window, "UpdatesTitle").ShouldBe("Загружаем 0.2.0");
        Text(window, "UpdateBadge").ShouldBe("Загрузка");
        Text(window, "UpdateProgress").ShouldBe("18,4 из 28,7 МБ · VPN продолжает работать");
        Find<Button>(window, "UpdateNow").IsEnabled.ShouldBeFalse("пока качается, второй раз не начать");
        Find<Button>(window, "PostponeUpdate").IsEffectivelyVisible.ShouldBeFalse();
        Snapshot(window, "about-update-loading");

        gate.SetResult();
        WaitFor(() => page.IsUpdateReady);
        Text(window, "UpdatesTitle").ShouldBe("Обновление готово");
        Text(window, "UpdateBadge").ShouldBe("Готово");
        Text(window, "UpdateProgress").ShouldStartWith("Установится при перезапуске клиента; блок при обрыве");
        Find<Button>(window, "UpdateNow").Content.ShouldBe("Перезапустить клиент");
        Snapshot(window, "about-update-ready");

        Press(window, "UpdateNow");
        WaitFor(() => Relauncher.Pids.Count > 0);
        Relauncher.Pids.ShouldBe([4242], "окно откроется заново после установщика, которого запустила служба");
    }

    [AvaloniaFact]
    public void Failed_download_and_install_say_so_and_retry()
    {
        var (window, page, service) = OpenAboutWith(new UpdateOffer(UpdateState.Available, Time.GetUtcNow(), "0.2.0") { Installable = true });
        Service<SettingsService>().Update(s => s with { KillSwitch = false });
        service.Download = [new UpdateProgress(0, 100, Error: "SHA-256 не сходится")];

        Press(window, "UpdateNow");
        WaitFor(() => page.HasUpdateError);
        Text(window, "UpdateError").ShouldStartWith("Загрузить обновление не удалось");
        Find<Button>(window, "UpdateNow").Content.ShouldBe("Повторить");

        service.Download = [new UpdateProgress(100, 100, Ready: true)];
        Press(window, "UpdateNow");
        WaitFor(() => page.IsUpdateReady);
        Text(window, "UpdateProgress").ShouldStartWith("Установится при перезапуске клиента. Блок при обрыве выключен",
            customMessage: "без блока при обрыве — честно, что на время замены службы защиты нет");

        service.Installer = null;
        Press(window, "UpdateNow");
        WaitFor(() => page.HasUpdateError);
        Text(window, "UpdateError").ShouldStartWith("Поставить обновление не удалось");
        Relauncher.Pids.ShouldBeEmpty("установщик не запущен — окно не закрывается");
        Find<Button>(window, "UpdateNow").Content.ShouldBe("Повторить");
    }

    [AvaloniaFact]
    public void Latest_version_unsigned_and_failed_checks_say_so_and_check_again_past_the_cache()
    {
        var (window, page, service) = OpenAboutWith(new UpdateOffer(UpdateState.UpToDate, Time.GetUtcNow()));

        Text(window, "UpdatesText").ShouldBe("Установлена последняя версия · проверено в 15:42");
        Press(window, "CheckUpdatesNow");
        Dispatcher.UIThread.RunJobs();
        service.UpdateChecks.Last().ShouldBe((UpdateChannel.Stable, true), "кнопка «Проверить» — мимо памяти службы");

        service.Update = new UpdateOffer(UpdateState.NotSigned, Time.GetUtcNow());
        page.CheckUpdatesNowCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Text(window, "UpdatesText").ShouldStartWith("Проверять и ставить обновления программа начнет с первого подписанного выпуска");
        Find<Button>(window, "CheckUpdatesNow").IsEffectivelyVisible.ShouldBeFalse("без ключа подписи проверять нечего");

        service.Update = new UpdateOffer(UpdateState.Failed, Time.GetUtcNow());
        page.CheckUpdatesNowCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Text(window, "UpdatesText").ShouldStartWith("Проверить обновления не удалось");
        Find<Button>(window, "CheckUpdatesNow").IsEffectivelyVisible.ShouldBeTrue();

        service.Update = null;
        page.CheckUpdatesNowCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Text(window, "UpdatesText").ShouldStartWith("Проверить обновления не удалось", customMessage: "служба без CheckForUpdate (старая) — тоже неудача");
    }

    [AvaloniaFact]
    public void Without_automatic_checks_the_window_asks_only_by_the_button()
    {
        ScriptedService? service = null;
        var (window, shell) = Open(gateway: sp => service = new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed)
        {
            Update = new UpdateOffer(UpdateState.UpToDate, Time.GetUtcNow()),
        });
        Service<SettingsService>().Update(s => s with { CheckUpdates = false });
        shell.NavigateCommand.Execute(Pages.About);
        Dispatcher.UIThread.RunJobs();

        service!.UpdateChecks.ShouldBeEmpty("«Проверять сами» выключено — окно за обновлениями в сеть не ходит");
        Text(window, "UpdatesText").ShouldStartWith("Сама программа обновления не проверяет");
        Find<ToggleButton>(window, "CheckUpdatesToggle").IsChecked.ShouldBe(false);

        Press(window, "CheckUpdatesNow");
        Dispatcher.UIThread.RunJobs();
        service.UpdateChecks.ShouldBe([(UpdateChannel.Stable, true)]);

        Find<ToggleButton>(window, "CheckUpdatesToggle").IsChecked = true;
        Dispatcher.UIThread.RunJobs();
        Service<SettingsService>().Current.CheckUpdates.ShouldBeTrue();
    }

    [AvaloniaFact]
    public void Update_channel_is_saved()
    {
        var (window, _, page) = OpenAbout();

        Press(window, "ChannelChoice");
        page.IsChannelOpen.ShouldBeTrue();
        page.ChannelOptions.Select(o => o.Label).ShouldBe(["Стабильный", "Бета"]);
        page.ChannelOptions[1].ChooseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        page.IsChannelOpen.ShouldBeFalse();
        Text(window, "ChannelText").ShouldBe("Бета");
        Service<SettingsService>().Current.UpdateChannel.ShouldBe(UpdateChannel.Beta);
    }

    [AvaloniaFact]
    public void Links_open_licenses_privacy_and_the_check_screen()
    {
        var (window, shell, _) = OpenAbout();

        Press(window, "LicensesLink");
        var licenses = shell.Overlay.Sheet.ShouldBeOfType<AboutSheetViewModel>();
        licenses.Title.ShouldBe("Лицензии открытых компонентов");
        licenses.Components.ShouldContain(c => c.Name == "Avalonia" && c.Label == "MIT");
        licenses.Components.ShouldContain(c => c.Name == "SQLite" && c.Label == "Public domain", "непонятный людям SPDX — подписью");
        Dispatcher.UIThread.RunJobs();
        Snapshot(window, "about-licenses");
        licenses.CloseCommand.Execute(null);

        Press(window, "PrivacyLink");
        var privacy = shell.Overlay.Sheet.ShouldBeOfType<AboutSheetViewModel>();
        (privacy.Title, privacy.HasComponents).ShouldBe(("Политика конфиденциальности", false));
        privacy.Text.ShouldContain("не собирает телеметрию");
        privacy.CloseCommand.Execute(null);

        Press(window, "ReportLink");
        Dispatcher.UIThread.RunJobs();
        shell.CurrentKey.ShouldBe(Pages.Check, "отчет для поддержки — на экране «Проверка»");
    }

    [AvaloniaFact]
    public void Older_service_shows_its_cores_says_what_happened_and_is_updated_from_this_copy()
    {
        ScriptedService? service = null;
        Installer.IsAvailable = true;
        var (window, shell) = Open(gateway: sp => service = new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed)
        {
            Details = new ServiceDetails("0.0.9", ContractMapping.ApiVersion, [new CoreDetails("xray", "25.8.3", true), new CoreDetails("sing-box", "1.14.3", true)]),
        });
        Installer.OnInstalled = () => service!.Details = service.Details! with { Version = AppInfo.Version };
        shell.NavigateCommand.Execute(Pages.About);
        Dispatcher.UIThread.RunJobs();
        var page = shell.CurrentPage.ShouldBeOfType<AboutPageViewModel>();

        Text(window, "AboutXray").ShouldBe("25.8.3", "версии ядер — от службы");
        Text(window, "TunDriver").ShouldStartWith("sing-box 1.14.3 · ", customMessage: "sing-box — движок TUN, а не второе ядро");
        Text(window, "AboutSecondCore").ShouldBe("не выбрано");
        Text(window, "ServiceState").ShouldBe("работает · 0.0.9");
        page.ServiceNotice.ShouldStartWith($"Служба 0.0.9 старше окна {AppInfo.Version}");
        Find<Button>(window, "UpdateService").IsEffectivelyVisible.ShouldBeTrue("кнопка — рядом с объяснением");
        Snapshot(window, "about-service-older");

        Press(window, "UpdateService");
        Dispatcher.UIThread.RunJobs();

        Installer.Installs.ShouldBe(1, "та же установка, что «Установить службу»: из этой копии программы");
        page.HasServiceNotice.ShouldBeFalse("после переустановки версии совпадают");
        Text(window, "ServiceState").ShouldBe($"работает · {AppInfo.Version}");

        service!.Details = service.Details! with { Version = "0.0.8" };
        Installer.Result = ServiceInstallResult.Failed;
        page.UpdateServiceCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        page.ServiceNotice.ShouldStartWith("Службу обновить не удалось", customMessage: "неудача — словами, а не молча");
    }

    [Fact]
    public void License_list_covers_every_package_the_app_ships()
    {
        // Пакеты, которые подключают сами проекты; пришедшие с ними сверяет со списком сборка выпуска по SBOM (scripts/package.cs).
        var shipped = Directory.GetFiles(Path.Combine(Repo.Root, "desktop", "src"), "*.csproj", SearchOption.AllDirectories)
            .SelectMany(p => XDocument.Load(p).Descendants("PackageReference"))
            .Where(r => (string?)r.Attribute("PrivateAssets") != "all")
            .Select(r => (string)r.Attribute("Include")!)
            .ToHashSet();

        shipped.Where(p => !AboutPageViewModel.Components.Any(c => c.Covers(p))).ShouldBeEmpty("пакет в программе есть, а его лицензии на экране нет");
        AboutPageViewModel.Components.Where(c => c.Packages.Count == 0 && c.Files.Count == 0 && c.Core is null && c.Data is null)
            .Select(c => c.Name).ShouldBeEmpty("компонент ни к чему в программе не привязан");
    }

    [Fact]
    public void License_list_covers_every_font_the_window_carries()
    {
        var src = Path.Combine(Repo.Root, "desktop", "src");
        var fonts = Directory.GetFiles(Path.Combine(src, "Client.UI", "Assets"), "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(src, f).Replace('\\', '/'))
            .ToList();

        fonts.ShouldNotBeEmpty();
        fonts.Where(f => !AboutPageViewModel.Components.Any(c => c.Files.Any(pattern => Matches(pattern, f))))
            .ShouldBeEmpty("шрифт в окне есть, а его лицензии на экране нет");
    }

    [Fact]
    public void Every_license_has_its_full_text_and_every_component_its_copyright()
    {
        var texts = Path.Combine(Repo.Root, "shared", "licenses", "texts");
        var used = AboutPageViewModel.Components.SelectMany(c => LicenseIds(c.License)).Distinct().ToList();

        used.Where(id => !File.Exists(Path.Combine(texts, TextName(id)))).ShouldBeEmpty("лицензия без полного текста в shared/licenses/texts");
        Directory.GetFiles(texts).Select(Path.GetFileName).Except(used.Select(TextName)).ShouldBeEmpty("текст лицензии, которой ни у кого нет");
        AboutPageViewModel.Components.Where(c => string.IsNullOrWhiteSpace(c.Copyright) || !c.Url.StartsWith("https://", StringComparison.Ordinal))
            .Select(c => c.Name).ShouldBeEmpty("у компонента нет строки авторства или ссылки — THIRD-PARTY-NOTICES без них неполный");
    }

    [Fact]
    public void License_list_covers_every_core_the_service_runs()
    {
        // Ядро — то, у чего в shared/cores закреплена версия: xray.version, sing-box.version.
        var cores = Directory.GetFiles(Path.Combine(Repo.Root, "shared", "cores"), "*.version").Select(f => Path.GetFileNameWithoutExtension(f)).ToList();

        cores.ShouldNotBeEmpty();
        cores.Where(core => !AboutPageViewModel.Components.Any(c => c.Core == core)).ShouldBeEmpty("ядро в службе есть, а его лицензии на экране нет");
        AboutPageViewModel.Components.Single(c => c.Core == "sing-box").License.ShouldBe("GPL-3.0-or-later");
    }

    // Выражение SPDX → идентификаторы: «MIT OR Apache-2.0» → MIT, Apache-2.0.
    private static IEnumerable<string> LicenseIds(string expression) =>
        expression.Replace("(", " ", StringComparison.Ordinal).Replace(")", " ", StringComparison.Ordinal)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t is not ("OR" or "AND" or "WITH"));

    // Текст GPL один на «-only» и «-or-later»: разница — в строке разрешения, а не в тексте.
    private static string TextName(string id) => id.Replace("-only", string.Empty, StringComparison.Ordinal).Replace("-or-later", string.Empty, StringComparison.Ordinal) + ".txt";

    private static bool Matches(string pattern, string path) =>
        pattern.EndsWith('*') ? path.StartsWith(pattern[..^1], StringComparison.Ordinal) : path == pattern;

    /// <summary>«О программе» с установленной службой, которая на вопрос об обновлении отвечает <paramref name="offer"/>.</summary>
    private (MainWindow Window, AboutPageViewModel Page, ScriptedService Service) OpenAboutWith(UpdateOffer? offer)
    {
        ScriptedService? service = null;
        var (window, shell) = Open(gateway: sp => service = new ScriptedService(Time, () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed)
        {
            Update = offer,
        });
        shell.NavigateCommand.Execute(Pages.About);
        Dispatcher.UIThread.RunJobs();
        return (window, shell.CurrentPage.ShouldBeOfType<AboutPageViewModel>(), service!);
    }

    private (MainWindow Window, ShellViewModel Shell, AboutPageViewModel Page) OpenAbout()
    {
        var (window, shell) = Open();
        shell.NavigateCommand.Execute(Pages.About);
        Dispatcher.UIThread.RunJobs();
        return (window, shell, shell.CurrentPage.ShouldBeOfType<AboutPageViewModel>());
    }
}
