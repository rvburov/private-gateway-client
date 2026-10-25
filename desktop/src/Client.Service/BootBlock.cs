using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Service;

/// <summary>
/// «Защита при загрузке»: политика блока, которую служба ставит при запуске, если защита была включена, когда
/// машина выключилась или служба остановилась. Туннеля при запуске нет, sing-box не работает — в файле только
/// сервер и настройки локальной сети и DNS. Файл лежит в папке службы: туда пишут только администраторы и root.
/// </summary>
public static class BootBlock
{
    /// <summary>Запомнить политику (без туннеля и обхода); запись — через временный файл.</summary>
    public static void Save(string path, KillSwitchPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var json = new JsonObject
        {
            ["servers"] = new JsonArray([.. policy.Servers.Select(s => (JsonNode)new JsonObject { ["address"] = s.Address.ToString(), ["port"] = s.Port })]),
            ["allowLan"] = policy.AllowLan,
            ["lanDiscovery"] = policy.LanDiscovery,
            ["blockOutsideDns"] = policy.BlockOutsideDns,
        };
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json.ToJsonString());
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Запомненная политика — постоянная, без туннеля; файла нет или он испорчен — null.</summary>
    public static KillSwitchPolicy? Load(string path)
    {
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject json)
            {
                return null;
            }

            var servers = json["servers"]!.AsArray()
                .Select(s => new KillSwitchEndpoint(IPAddress.Parse(s!["address"]!.GetValue<string>()), s["port"]!.GetValue<int>())).ToList();
            return new KillSwitchPolicy(null, servers, json["allowLan"]!.GetValue<bool>(), json["lanDiscovery"]!.GetValue<bool>(),
                json["blockOutsideDns"]!.GetValue<bool>(), Persistent: true);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or NullReferenceException or IOException)
        {
            return null;
        }
    }

    /// <summary>Защиту выключили — после перезагрузки блока не будет.</summary>
    public static void Forget(string path) => File.Delete(path);
}
