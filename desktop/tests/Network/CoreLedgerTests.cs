using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Grpc.Core;
using PrivateGateway.Client.Infrastructure.Service;
using PrivateGateway.Client.Service;
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

    [Fact]
    public async Task Second_copy_of_the_service_on_the_same_state_does_not_start_and_leaves_the_first_alone()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite("через сервер");
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
        serverConfig["outbounds"] = new JsonArray(
            new JsonObject { ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{site.Url.Port}" } });
        using var server = await XrayKit.Start(serverConfig, serverPort);
        string Endpoint() => OperatingSystem.IsWindows() ? "PrivateGateway.Test." + Guid.NewGuid().ToString("N") : Path.Combine(_root, Guid.NewGuid().ToString("N")[..8] + ".sock");
        var endpoint = Endpoint();
        StartService(endpoint);
        await ConnectAsync(endpoint, ServiceIpcTests.Request(definition, XrayKit.FreePort()));
        var pid = JsonNode.Parse(File.ReadAllText(CoreLedger.In(_root)))!.AsArray().ShouldHaveSingleItem()!["pid"]!.GetValue<int>();

        var second = StartService(Endpoint());
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        wait.CancelAfter(TimeSpan.FromSeconds(30));
        await second.WaitForExitAsync(wait.Token);

        second.ExitCode.ShouldBe(2, "вторая копия на той же папке состояния не работает (PGC-SVC-009)");
        Process.GetProcessById(pid).HasExited.ShouldBeFalse("ядро первой копии вторая не тронула");
        using var channel = IpcChannel.Create(endpoint, system: false);
        (await new V1.GatewayService.GatewayServiceClient(channel).GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: Ct)).State
            .ShouldBe(V1.ConnectionState.Connected, "первая копия работает дальше");
    }

    [Fact]
    public async Task Service_after_a_crash_shows_what_was_before_and_what_it_restored()
    {
        Assert.SkipWhen(XrayKit.Xray is null, "нет сборки Xray — scripts/get-xray.sh");
        using var site = new LocalSite("через сервер");
        var serverPort = XrayKit.FreePort();
        var (definition, serverConfig) = XrayKit.Pair("vless", serverPort);
        serverConfig["outbounds"] = new JsonArray(
            new JsonObject { ["protocol"] = "freedom", ["settings"] = new JsonObject { ["redirect"] = $"127.0.0.1:{site.Url.Port}" } });
        using var server = await XrayKit.Start(serverConfig, serverPort);
        var endpoint = OperatingSystem.IsWindows() ? "PrivateGateway.Test." + Guid.NewGuid().ToString("N") : Path.Combine(_root, "ipc", "ipc.sock");

        var first = StartService(endpoint);
        await ConnectAsync(endpoint, ServiceIpcTests.Request(definition, XrayKit.FreePort()));
        first.Kill();
        await first.WaitForExitAsync(Ct);

        // После аварии окно видит записи прошлого запуска, строку об аварии и сводку восстановления.
        var window = Started(StandIn());
        StartService(endpoint, window.Id);
        var log = await LogUntilAsync(endpoint, m => m.StartsWith("Восстановление: ", StringComparison.Ordinal));
        log.ShouldContain("Подключено: прокси на 127.0.0.1 готов", "запись до аварии — с диска");
        log.ShouldContain("Прошлый запуск службы завершился аварийно — восстановление");
        log[^1].ShouldStartWith("Восстановление: ядер прошлого запуска снято — ");
        log[^1].ShouldEndWith("блока при обрыве нет");

        // Штатная остановка (закрылось окно) — следующий запуск аварии не видит. На Windows в CI остановка однажды заняла
        // больше 20 с: ждать с запасом, а при сбое показать журнал службы с диска — по времени записей видно, где она стояла.
        window.Kill();
        await WaitForAsync(() => !File.Exists(Path.Combine(_root, "running")), TimeSpan.FromSeconds(60), JournalOnDisk);
        StartService(endpoint);
        var after = await LogUntilAsync(endpoint, m => m.StartsWith("Служба ", StringComparison.Ordinal) && m.Contains("слушает IPC", StringComparison.Ordinal),
            skip: log.Count);
        after.Count(m => m == "Прошлый запуск службы завершился аварийно — восстановление").ShouldBe(1, "аварийной была только первая остановка");
    }

    /// <summary>
    /// Записи журнала службы с начала (поток WatchLog с хвостом), пока не придет запись, для которой <paramref name="until"/> — правда,
    /// не раньше <paramref name="skip"/> записей; служба еще запускается — ждать ее.
    /// </summary>
    private static async Task<List<string>> LogUntilAsync(string endpoint, Func<string, bool> until, int skip = 0)
    {
        using var channel = IpcChannel.Create(endpoint, system: false);
        var client = new V1.GatewayService.GatewayServiceClient(channel);
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        wait.CancelAfter(TimeSpan.FromSeconds(30));
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await client.GetStatusAsync(new V1.GetStatusRequest(), cancellationToken: wait.Token);
                break;
            }
            catch (RpcException) when (attempt < 100)
            {
                await Task.Delay(200, wait.Token);
            }
        }

        using var log = client.WatchLog(new V1.WatchLogRequest { Tail = ServiceJournal.Capacity }, cancellationToken: wait.Token);
        var messages = new List<string>();
        while (await log.ResponseStream.MoveNext(wait.Token))
        {
            messages.Add(log.ResponseStream.Current.Message);
            if (messages.Count > skip && until(messages[^1]))
            {
                return messages;
            }
        }

        throw new InvalidOperationException("журнал оборвался");
    }

    /// <summary>Процесс, который ждет, пока его не закроют, — «окно» для службы пользователя.</summary>
    private static ProcessStartInfo StandIn() => OperatingSystem.IsWindows()
        ? new ProcessStartInfo("ping") { ArgumentList = { "-n", "600", "127.0.0.1" } }
        : new ProcessStartInfo("sleep") { ArgumentList = { "600" } };

    private static async Task WaitForAsync(Func<bool> done) => await WaitForAsync(done, TimeSpan.FromSeconds(20), () => string.Empty);

    private static async Task WaitForAsync(Func<bool> done, TimeSpan limit, Func<string> why)
    {
        for (var waited = Stopwatch.StartNew(); !done(); await Task.Delay(100, Ct))
        {
            waited.Elapsed.ShouldBeLessThan(limit, why());
        }
    }

    /// <summary>Журнал службы с диска — читать, как любая программа, с общим доступом на запись.</summary>
    private string JournalOnDisk()
    {
        var path = Path.Combine(_root, "journal.jsonl");
        if (!File.Exists(path))
        {
            return "журнала на диске нет";
        }

        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
        return "журнал службы:\n" + string.Join('\n', reader.ReadToEnd().Split('\n').TakeLast(15));
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

    /// <summary>
    /// Служба пользователя отдельным процессом — ее можно убить, как при сбое; <paramref name="parent"/> — процесс «окна»: закроется
    /// он — служба остановится штатно.
    /// </summary>
    private Process StartService(string endpoint, int? parent = null)
    {
        var executable = Path.Combine(XrayKit.Root, "artifacts", "bin", "Client.Service", new DirectoryInfo(AppContext.BaseDirectory).Name,
            UserServiceLauncher.FileName);
        // Проверочный адрес — по HTTP: сервер теста отдает любой адрес своему сайту, и «Защищено» приходит после ответа сервера.
        var start = new ProcessStartInfo(executable)
        {
            ArgumentList = { "--user", "--ipc", endpoint, "--state", _root, "--xray", XrayKit.Xray!, "--latency-target", "http://example.com/" },
        };
        if (parent is { } id)
        {
            start.ArgumentList.Add("--parent");
            start.ArgumentList.Add(id.ToString(CultureInfo.InvariantCulture));
        }

        return Started(start);
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
