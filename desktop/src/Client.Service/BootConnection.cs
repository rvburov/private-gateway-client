using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;
using PrivateGateway.Client.Core.Platform;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Service;

/// <summary>
/// Последнее подключение для «Защиты при загрузке»: запрос окна (сервер с ключами и настройки) и адреса
/// сервера, найденные при подключении, — одним файлом, зашифрованным хранилищем ОС (<see cref="ISecretSealer"/>).
/// При загрузке служба подключается по нему сама, до входа пользователя; адреса — заранее найденные: при
/// загрузке сети может еще не быть. Открытым текстом ключи на диск не попадают.
/// </summary>
public sealed class BootConnection(string path, ISecretSealer sealer)
{
    /// <summary>Чем зашифровано — для журнала.</summary>
    public string Sealer => sealer.Name;

    /// <summary>Запомнить подключение; запись — через временный файл, доступный только службе.</summary>
    public void Save(V1.ConnectRequest request, IReadOnlyList<IPAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(addresses);
        var plain = new JsonObject
        {
            ["request"] = Convert.ToBase64String(request.ToByteArray()),
            ["addresses"] = new JsonArray([.. addresses.Select(a => (JsonNode)a.ToString())]),
        };
        var sealedData = sealer.Seal(Encoding.UTF8.GetBytes(plain.ToJsonString()));
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, sealedData);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Запомненное подключение; файла нет, он испорчен или не расшифровался — null.</summary>
    public (V1.ConnectRequest Request, IReadOnlyList<IPAddress> Addresses)? Load()
    {
        try
        {
            if (!File.Exists(path) || sealer.Unseal(File.ReadAllBytes(path)) is not { } plain
                || JsonNode.Parse(Encoding.UTF8.GetString(plain)) is not JsonObject json)
            {
                return null;
            }

            var request = V1.ConnectRequest.Parser.ParseFrom(Convert.FromBase64String(json["request"]!.GetValue<string>()));
            IReadOnlyList<IPAddress> addresses = [.. json["addresses"]!.AsArray().Select(a => IPAddress.Parse(a!.GetValue<string>()))];
            return addresses.Count > 0 ? (request, addresses) : null;
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or NullReferenceException or IOException
            or InvalidProtocolBufferException)
        {
            return null;
        }
    }

    /// <summary>Защиту выключили — при загрузке не подключаться.</summary>
    public void Forget() => File.Delete(path);
}
