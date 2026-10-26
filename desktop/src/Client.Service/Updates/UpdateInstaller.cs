using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Updates;

namespace PrivateGateway.Client.Service.Updates;

/// <summary>Чем ставить обновление: файл выпуска (ключ манифеста) и файл, которым он ставится.</summary>
/// <param name="Asset">Ключ файла в манифесте: win-x64-msi, linux-x64-deb, linux-x64-rpm.</param>
/// <param name="Launch">Запустить установщик ОС для скачанного файла (путь, журнал установки) → номер процесса.</param>
public sealed record UpdateKind(string Asset, Func<string, string, int> Launch);

/// <summary>
/// Обновление программы службой: файл своей ОС из подписанного манифеста скачивается в папку состояния службы
/// (<c>updates</c>, пишет туда только служба) и сверяется с манифестом — размер и SHA-256; потом установщик ОС
/// запускается отдельно от службы — он заменит и ее (MSI и пакеты Linux ставят службу атомарно, ServiceSetup).
/// Ставит только установленная служба программы, поставленной установщиком: у архива и службы пользователя
/// <see cref="Kind"/> — null, окно ведет на страницу выпуска.
/// </summary>
public sealed class UpdateInstaller(HttpClient http, Uri downloads, string folder, UpdateKind? kind, ServiceJournal journal)
{
    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(250);

    public UpdateKind? Kind => kind;

    /// <summary>
    /// Как поставлена программа на этой машине: Windows — MSI (ключ реестра его ярлыка), Linux — пакет .deb (dpkg его
    /// знает) или .rpm (rpm его знает). Только для установленной службы с правами; иначе — null.
    /// </summary>
    public static UpdateKind? ForThisMachine(bool installedService, string? ownerSid)
    {
        if (!installedService || !Environment.IsPrivilegedProcess || RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            return null;
        }

        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Private Gateway");
            return key?.GetValue("StartMenuShortcut") is not null && ownerSid is not null
                ? new UpdateKind("win-x64-msi", (file, log) => Start("msiexec.exe", ["/i", file, "/qn", "/norestart", "OWNER=" + ownerSid, "/l*v", log]))
                : null;
        }

        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/systemd-run"))
        {
            return null;
        }

        if (File.Exists("/var/lib/dpkg/info/private-gateway.list"))
        {
            var apt = File.Exists("/usr/bin/apt-get")
                ? "/usr/bin/apt-get install -y --allow-downgrades -o Dpkg::Options::=--force-confold"
                : "/usr/bin/dpkg -i";
            return new UpdateKind("linux-x64-deb", (file, log) => Detached(apt, file, log));
        }

        return File.Exists("/usr/bin/rpm") && Start("/usr/bin/rpm", ["-q", "--quiet", "private-gateway"], wait: true) == 0
            ? new UpdateKind("linux-x64-rpm", (file, log) => Detached("/usr/bin/rpm -U --oldpackage --replacepkgs", file, log))
            : null;
    }

    /// <summary>Скачать файл выпуска своей ОС и сверить с манифестом; поток — ход загрузки, последнее — готово или отказ.</summary>
    public async IAsyncEnumerable<UpdateProgress> DownloadAsync(UpdateManifest manifest, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (kind is null || !manifest.Assets.TryGetValue(kind.Asset, out var asset))
        {
            yield return new UpdateProgress(0, 0, Error: "в выпуске нет файла для этой установки");
            yield break;
        }

        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, asset.File);
        if (File.Exists(target) && Matches(target, asset))
        {
            yield return new UpdateProgress(asset.Size, asset.Size, Ready: true);
            yield break;
        }

        foreach (var stale in Directory.GetFiles(folder))
        {
            File.Delete(stale);
        }

        var url = new Uri(downloads, $"v{manifest.Version}/{asset.File}");
        var part = target + ".part";
        var channel = System.Threading.Channels.Channel.CreateUnbounded<UpdateProgress>();
        var download = Task.Run(async () =>
        {
            try
            {
                await DownloadFileAsync(url, part, asset, p => channel.Writer.TryWrite(p), cancellationToken).ConfigureAwait(false);
                File.Move(part, target, overwrite: true);
                journal.Add(JournalLevel.Info, "update", $"Обновление {manifest.Version} скачано и сверено с манифестом ({asset.File})");
                channel.Writer.TryWrite(new UpdateProgress(asset.Size, asset.Size, Ready: true));
            }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidDataException
                || (e is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                File.Delete(part);
                journal.Add(JournalLevel.Warning, "update", $"Обновление {manifest.Version} не скачалось: {e.Message}");
                channel.Writer.TryWrite(new UpdateProgress(0, asset.Size, Error: e.Message));
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, cancellationToken);

        await foreach (var progress in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return progress;
        }

        await download.ConfigureAwait(false);
    }

    /// <summary>
    /// Поставить скачанное: файл сверяется с манифестом еще раз, установщик ОС запускается отдельно от службы.
    /// </summary>
    /// <returns>Номер процесса установщика.</returns>
    /// <exception cref="IOException">Файла нет, он не тот или установщик не запустился.</exception>
    public int Install(UpdateManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (kind is null || !manifest.Assets.TryGetValue(kind.Asset, out var asset))
        {
            throw new IOException("в выпуске нет файла для этой установки");
        }

        var file = Path.Combine(folder, asset.File);
        if (!File.Exists(file) || !Matches(file, asset))
        {
            throw new IOException("обновление не скачано или файл не совпадает с манифестом");
        }

        var pid = kind.Launch(file, Path.Combine(folder, "install.log"));
        journal.Add(JournalLevel.Info, "update", $"Ставится обновление {manifest.Version}: установщик ОС, процесс {pid}");
        return pid;
    }

    private async Task DownloadFileAsync(Uri url, string part, UpdateAsset asset, Action<UpdateProgress> report, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
        {
            throw new InvalidDataException($"{asset.File}: {length} байт, а в манифесте {asset.Size}");
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (source.ConfigureAwait(false))
        {
            var output = File.Create(part);
            await using (output.ConfigureAwait(false))
            {
                var buffer = new byte[81920];
                long received = 0;
                var reported = Stopwatch.StartNew();
                report(new UpdateProgress(0, asset.Size));
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > asset.Size)
                    {
                        throw new InvalidDataException($"{asset.File}: больше {asset.Size} байт из манифеста");
                    }

                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    if (reported.Elapsed >= ReportEvery)
                    {
                        report(new UpdateProgress(received, asset.Size));
                        reported.Restart();
                    }
                }

                if (received != asset.Size || Convert.ToHexStringLower(hash.GetHashAndReset()) != asset.Sha256)
                {
                    throw new InvalidDataException($"{asset.File}: SHA-256 или размер не сходятся с подписанным манифестом");
                }
            }
        }
    }

    private static bool Matches(string file, UpdateAsset asset)
    {
        using var stream = File.OpenRead(file);
        return stream.Length == asset.Size && Convert.ToHexStringLower(SHA256.HashData(stream)) == asset.Sha256;
    }

    // Linux: пакетный менеджер — в своей области systemd (systemd-run --scope), а не в службе: установка перезапускает
    // службу, и systemd снял бы вместе с ней и установку. Вывод — в журнал установки, не в канал службы, которой скоро не станет.
    private static int Detached(string command, string file, string log) => Start("/usr/bin/systemd-run",
        ["--scope", "--quiet", "--collect", "--unit=private-gateway-update-" + Environment.TickCount64, "--", "/bin/sh", "-c",
            command + " \"$1\" > \"$2\" 2>&1", "sh", file, log],
        environment: ("DEBIAN_FRONTEND", "noninteractive"));

    private static int Start(string program, IReadOnlyList<string> arguments, bool wait = false, (string Name, string Value)? environment = null)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (environment is var (name, value))
        {
            start.Environment[name] = value;
        }

        try
        {
            using var process = Process.Start(start) ?? throw new IOException(program + " не запустился");
            if (!wait)
            {
                return process.Id;
            }

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new IOException(program + " не запустился: " + e.Message, e);
        }
    }
}
