using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Application.Settings;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.UI.Services;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>Отчет различает установленную службу и окно даже при общей версии выпуска.</summary>
public sealed class ReportBuildIdentityTests : WindowHarness
{
    private const string ServiceBuild = "fedcba9876543210fedcba9876543210fedcba98";

    [AvaloniaFact]
    public async Task Export_names_window_and_service_builds_separately_without_anonymizing_commits()
    {
        var (_, shell) = Open(gateway: sp => new ScriptedService(Time,
            () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed)
        {
            Details = new ServiceDetails("0.0.7", "1", [], ServiceBuild),
        });
        var page = OpenReport(shell);

        await page.ExportCommand.ExecuteAsync(null);

        var report = Files.Text.ShouldNotBeNull();
        var windowBuild = string.IsNullOrWhiteSpace(AppInfo.Build) ? "неизвестно" : AppInfo.Build;
        report.ShouldContain($"Сборка окна: версия {AppInfo.Version} · коммит {windowBuild}");
        report.ShouldContain($"Сборка службы: версия 0.0.7 · коммит {ServiceBuild} · IPC 1");
        report.ShouldContain($"Платформа окна: {AppInfo.OsVersion} · {AppInfo.Architecture}");
        report.ShouldContain("Отчет обезличен", customMessage: "метаданные сохраняются в обычном обезличенном отчете");
    }

    [AvaloniaFact]
    public async Task Export_does_not_reuse_old_service_identity_when_the_service_no_longer_answers()
    {
        ScriptedService? service = null;
        var (_, shell) = Open(gateway: sp => service = new ScriptedService(Time,
            () => sp.GetRequiredService<SettingsService>().Current, ServiceKind.Installed)
        {
            Details = new ServiceDetails("0.0.7", "1", [], ServiceBuild),
        });
        var page = OpenReport(shell);
        await page.ExportCommand.ExecuteAsync(null);
        Files.Text.ShouldNotBeNull().ShouldContain(ServiceBuild);

        service!.Details = null;
        await page.ExportCommand.ExecuteAsync(null);

        var report = Files.Text.ShouldNotBeNull();
        report.ShouldContain("Сборка службы: версия неизвестно · коммит неизвестно · IPC неизвестно");
        report.ShouldNotContain(ServiceBuild, customMessage: "старый ответ службы не выдается за текущий");

        service.Details = new ServiceDetails("0.1.0", "1", []);
        await page.ExportCommand.ExecuteAsync(null);
        Files.Text.ShouldNotBeNull().ShouldContain("Сборка службы: версия 0.1.0 · коммит неизвестно · IPC 1");
    }

    private static CheckPageViewModel OpenReport(ShellViewModel shell)
    {
        shell.NavigateCommand.Execute(Pages.Check);
        Dispatcher.UIThread.RunJobs();
        var page = shell.CurrentPage.ShouldBeOfType<CheckPageViewModel>();
        WaitFor(() => !page.IsRunning);
        return page;
    }
}
