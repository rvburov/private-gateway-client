using PrivateGateway.Client.Application.Secrets;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Хранилище секретов в памяти: можно выключить целиком или дать ему отказать на N-й записи.</summary>
internal sealed class FakeSecrets : ISecretStore
{
    public Dictionary<string, string> Values { get; } = [];

    public bool Available { get; set; } = true;

    public string Name => "Тестовое хранилище";

    public bool IsAvailable => Available;

    /// <summary>Сколько записей пройдет до отказа хранилища; null — без отказа.</summary>
    public int? SavesLeft { get; set; }

    public void Save(string key, string secret)
    {
        if (!Available || SavesLeft-- <= 0)
        {
            throw new InvalidOperationException();
        }

        Values[key] = secret;
    }

    public string? Read(string key) => Values.GetValueOrDefault(key);

    public void Delete(string key) => Values.Remove(key);
}
