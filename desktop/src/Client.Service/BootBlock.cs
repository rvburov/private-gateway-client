using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Service;

/// <summary>
/// Блок при обрыве, который держит служба: политика, имя сервера, «Защита при загрузке» (<paramref name="Boot"/>)
/// и строгий режим (<paramref name="Strict"/>); <paramref name="Paused"/> — блок снят на паузу для входа в Wi-Fi;
/// <paramref name="Open"/> — «Блок при обрыве» выключен: правила «только туннель» стоят, пока туннель поднят, а при обрыве,
/// сбое и остановке снимаются. По нему служба возвращает тот же блок после паузы, узнает строгий режим после своего
/// перезапуска (а открытые правила снимает) и подключается к тому же серверу, когда имя под блоком не найти.
/// </summary>
public sealed record HeldBlock(KillSwitchPolicy Policy, string Host, bool Boot, bool Strict, bool Paused = false, bool Open = false);

/// <summary>
/// Файл блока, который держит служба (<see cref="HeldBlock"/>): пишется при каждом блоке, стирается, когда блок
/// сняли. При запуске служба ставит по нему блок, если он с «Защитой при загрузке» — защита была включена, когда
/// машина выключилась или служба остановилась. Туннеля при запуске нет, sing-box не работает — в файле только
/// сервер и настройки локальной сети и DNS. Файл лежит в папке службы, читать и писать его могут только служба и
/// администраторы: в нем сервер пользователя.
/// </summary>
public static class BootBlock
{
    /// <summary>Запомнить блок (политику — без туннеля и обхода); запись — через временный файл.</summary>
    public static void Save(string path, HeldBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        var policy = block.Policy;
        var json = new JsonObject
        {
            ["servers"] = new JsonArray([.. policy.Servers.Select(s => (JsonNode)new JsonObject { ["address"] = s.Address.ToString(), ["port"] = s.Port })]),
            ["allowLan"] = policy.AllowLan,
            ["lanDiscovery"] = policy.LanDiscovery,
            ["blockOutsideDns"] = policy.BlockOutsideDns,
            ["host"] = block.Host,
            ["boot"] = block.Boot,
            ["strict"] = block.Strict,
            ["paused"] = block.Paused,
            ["open"] = block.Open,
        };
        var temporary = path + ".tmp";
        var text = System.Text.Encoding.UTF8.GetBytes(json.ToJsonString());
        if (OperatingSystem.IsWindows())
        {
            WriteOwnerOnly(temporary, text);
        }
        else
        {
            File.WriteAllBytes(temporary, text);
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Файл с доступом только у SYSTEM, администраторов и самой службы — без наследования от папки.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void WriteOwnerOnly(string path, byte[] content)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[]
        {
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            WindowsIdentity.GetCurrent().User!,
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        using var file = new FileInfo(path).Create(FileMode.Create, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security);
        file.Write(content);
    }

    /// <summary>
    /// Запомненный блок; политика — без туннеля, постоянная, если он с «Защитой при загрузке». Файла нет или он
    /// испорчен — null. Файл без признаков (прежний) — с «Защитой при загрузке» и без строгого режима: другим его
    /// тогда не писали.
    /// </summary>
    public static HeldBlock? Read(string path)
    {
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject json)
            {
                return null;
            }

            var servers = json["servers"]!.AsArray()
                .Select(s => new KillSwitchEndpoint(IPAddress.Parse(s!["address"]!.GetValue<string>()), s["port"]!.GetValue<int>())).ToList();
            var boot = json["boot"]?.GetValue<bool>() ?? true;
            var policy = new KillSwitchPolicy(null, servers, json["allowLan"]!.GetValue<bool>(), json["lanDiscovery"]!.GetValue<bool>(),
                json["blockOutsideDns"]!.GetValue<bool>(), Persistent: boot);
            return new HeldBlock(policy, json["host"]?.GetValue<string>() ?? string.Empty, boot, json["strict"]?.GetValue<bool>() ?? false,
                json["paused"]?.GetValue<bool>() ?? false, json["open"]?.GetValue<bool>() ?? false);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or NullReferenceException or IOException)
        {
            return null;
        }
    }

    /// <summary>Политика «Защиты при загрузке» — постоянная, без туннеля; блок без нее перезагрузку не переживает — null.</summary>
    public static KillSwitchPolicy? Load(string path) => Read(path) is { Boot: true } block ? block.Policy : null;

    /// <summary>Блок сняли — после перезагрузки и перезапуска службы его не будет.</summary>
    public static void Forget(string path) => File.Delete(path);
}
