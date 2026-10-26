using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Grpc.Core;
using PrivateGateway.Client.Infrastructure.Service;
using PrivateGateway.Client.Service.Cores;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Ядра, пережившие службу: служба пользователя на Linux и macOS не в cgroup systemd и не в задании Windows,
/// и после ее сбоя ядро держит порт прокси. Следующий запуск снимает только свои ядра — по номеру, времени
/// запуска и пути программы.
/// </summary>
public sealed class CoreLedgerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pg-ledger-" + Guid.NewGuid().ToString("N")[..8]);

    // Ядра и службы, которые запустил тест: снимаются и при провале проверки — иначе держали бы вывод прогона.
    private readonly List<Process> _started = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var process in _started)
        {
            if (!process.HasExited)
            {
                // Снятие асинхронное: на Windows файл конфига свободен, только когда процесс вышел.
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(10));
            }

            process.Dispose();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Only_its_own_core_is_killed_and_the_file_is_emptied()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var path = CoreLedger.In(_root);
        var own = StartCore();
        var renamed = StartCore();
        var reborn = StartCore();
        var gone = StartCore();
        var ledger = new CoreLedger(path);
        ledger.Add(own, XrayKit.Xray!);
        ledger.Add(renamed, Path.Combine(_root, "other", "xray"));
        ledger.Add(reborn, XrayKit.Xray!);
        ledger.Add(gone, XrayKit.Xray!);
        gone.Kill();
        gone.WaitForExit();
        // Номер тот же, а процесс запущен в другое время — номер достался чужому процессу.
        var json = JsonNode.Parse(File.ReadAllText(path))!.AsArray();
        var entry = json.Single(e => e!["pid"]!.GetValue<int>() == reborn.Id)!;
        entry["started"] = reborn.StartTime.ToUniversalTime().AddHours(-1).ToString("O", CultureInfo.InvariantCulture);
        File.WriteAllText(path, json.ToJsonString());

        var killed = new CoreLedger(path).KillLeftovers();

        killed.ShouldBe([own.Id]);
        own.HasExited.ShouldBeTrue();
        renamed.HasExited.ShouldBeFalse("другая программа с тем же номером — не наше ядро");
        reborn.HasExited.ShouldBeFalse("процесс запущен в другое время — номер достался чужому");
        JsonNode.Parse(File.ReadAllText(path))!.AsArray().ShouldBeEmpty("после проверки файл пуст: чужие процессы в нем не остаются");
    }

    [Fact]
    public void Exited_core_is_forgotten()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        var path = CoreLedger.In(_root);
        var core = StartCore();
        var ledger = new CoreLedger(path);
        ledger.Add(core, XrayKit.Xray!);

        ledger.Remove(core.Id);

        new CoreLedger(path).KillLeftovers().ShouldBeEmpty();
        core.HasExited.ShouldBeFalse("забытое ядро не снимается");
    }

    [Fact]
    public async Task User_service_after_a_crash_takes_back_the_proxy_port()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite("через сервер");
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
        serverConfig["outbounds"] = new JsonArray(
            new JsonObject { ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{site.Url.Port}" } });
        using var server = await XrayKit.Start(serverConfig, serverPort);
        var endpoint = OperatingSystem.IsWindows() ? "PrivateGateway.Test." + Guid.NewGuid().ToString("N") : Path.Combine(_root, "ipc", "ipc.sock");
        var request = ServiceIpcTests.Request(definition, XrayKit.FreePort());

        var first = StartService(endpoint);
        await ConnectAsync(endpoint, request);
        var pid = JsonNode.Parse(File.ReadAllText(CoreLedger.In(_root)))!.AsArray().ShouldHaveSingleItem()!["pid"]!.GetValue<int>();
        var orphan = Process.GetProcessById(pid);
        _started.Add(orphan);
        first.Kill();
        await first.WaitForExitAsync(Ct);

        if (!OperatingSystem.IsWindows())
        {
            orphan.HasExited.ShouldBeFalse("служба пользователя убита — ядро осталось и держит порт");
        }

        StartService(endpoint);
        await ConnectAsync(endpoint, request);

        orphan.HasExited.ShouldBeTrue("новый запуск снял ядро прошлого");
        await DisconnectAsync(endpoint);
        JsonNode.Parse(File.ReadAllText(CoreLedger.In(_root)))!.AsArray().ShouldBeEmpty("ядро остановлено — служба его забыла");
    }

    /// <summary>Ядро Xray с одним входом SOCKS на свободном порту — долгий процесс, который тест закрывает сам.</summary>
    private Process StartCore()
    {
        var config = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(config, $$$"""
            {"inbounds":[{"listen":"127.0.0.1","port":{{{XrayKit.FreePort()}}},"protocol":"socks","settings":{"auth":"noauth"}}],
             "outbounds":[{"protocol":"freedom"}]}
            """);
        return Started(new ProcessStartInfo(XrayKit.Xray!) { ArgumentList = { "run", "-config", config } });
    }

    /// <summary>Служба пользователя отдельным процессом — ее можно убить, как при сбое.</summary>
    private Process StartService(string endpoint)
    {
        var executable = Path.Combine(XrayKit.Root, "artifacts", "bin", "Client.Service", new DirectoryInfo(AppContext.BaseDirectory).Name,
            UserServiceLauncher.FileName);
        // Проверочный адрес — по HTTP: сервер теста отдает любой адрес своему сайту, и «Защищено» приходит после ответа сервера.
        return Started(new ProcessStartInfo(executable)
        {
            ArgumentList = { "--user", "--ipc", endpoint, "--state", _root, "--xray", XrayKit.Xray!, "--latency-target", "http://example.com/" },
        });
    }

    /// <summary>Запустить с выводом в тест, а не в прогон, и запомнить — <see cref="Dispose"/> снимет.</summary>
    private Process Started(ProcessStartInfo info)
    {
        info.RedirectStandardOutput = info.RedirectStandardError = true;
        var process = Process.Start(info)!;
        _started.Add(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task DisconnectAsync(string endpoint)
    {
        using var channel = IpcChannel.Create(endpoint, system: false);
        var client = new V1.GatewayService.GatewayServiceClient(channel);
        using var watch = client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await client.DisconnectAsync(new V1.DisconnectRequest(), cancellationToken: Ct);
        await ServiceIpcTests.ReadUntilAsync(watch, s => s.State == V1.ConnectionState.Off);
    }

    /// <summary>Дождаться службы и подключиться в режиме прокси; «Ошибка» — провал теста с ее текстом.</summary>
    private static async Task ConnectAsync(string endpoint, V1.ConnectRequest request)
    {
        using var channel = IpcChannel.Create(endpoint, system: false);
        var client = new V1.GatewayService.GatewayServiceClient(channel);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct);
                break;
            }
            catch (RpcException) when (attempt < 100)
            {
                await Task.Delay(200, Ct);
            }
        }

        using var watch = client.WatchStatus(new V1.WatchStatusRequest(), cancellationToken: Ct);
        await client.ConnectAsync(request, cancellationToken: Ct);
        var done = (await ServiceIpcTests.ReadUntilAsync(watch, s => s.State is V1.ConnectionState.Connected or V1.ConnectionState.Error))[^1];
        done.State.ShouldBe(V1.ConnectionState.Connected, done.Error?.Message);
    }
}
