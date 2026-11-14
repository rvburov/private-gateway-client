using System.Security.Cryptography;
using System.Text.Json;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Updates;

namespace PrivateGateway.Client.Service.Updates;

/// <summary>
/// Последнее увиденное подписанное изменение каждого выпуска. Номер относится к правилам раскатки, а не к версии
/// установленной программы: осознанный откат программы не разрешает вернуть прежнюю политику ее выпуска.
/// Запись находится в папке состояния службы; без успешного сохранения новый манифест не используется.
/// </summary>
public sealed class UpdatePolicyHistory
{
    /// <summary>Отдельный файл, который обновление программы не удаляет вместе с кешем пакетов.</summary>
    public const string FileName = "update-policies.json";

    private const int MaxBytes = 1024 * 1024;
    private readonly Lock _gate = new();
    private readonly string? _path;
    private readonly Dictionary<string, Seen> _seen = new(StringComparer.Ordinal);
    private readonly string? _readError;
    private bool _dirty;

    /// <param name="state">Папка состояния службы; null — история только на этот запуск (тесты).</param>
    public UpdatePolicyHistory(string? state = null)
    {
        if (state is null)
        {
            return;
        }

        _path = Path.Combine(state, FileName);
        try
        {
            if (new FileInfo(_path).Length > MaxBytes)
            {
                throw new JsonException("История изменений слишком велика");
            }

            var saved = JsonSerializer.Deserialize<Dictionary<string, Seen>>(File.ReadAllText(_path));
            if (saved is null || saved.Any(p => !Valid(p.Key, p.Value)))
            {
                throw new JsonException("История изменений не читается");
            }

            _seen = new Dictionary<string, Seen>(saved, StringComparer.Ordinal);
        }
        catch (FileNotFoundException)
        {
            // Первый запуск с этой проверкой: история появится при первом подписанном манифесте.
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Испорченную или недоступную историю нельзя молча считать пустой: это разрешило бы повтор старого стоп-крана.
            _readError = "история изменений выпусков недоступна — обновление не проверено";
        }
    }

    /// <summary>Вызывать только после проверки подписи и полей манифеста. null — принят; иначе причина отказа.</summary>
    public string? Accept(UpdateManifest manifest, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(bytes);
        lock (_gate)
        {
            if (_readError is not null)
            {
                return _readError;
            }

            var identity = manifest.Channel.ToString().ToLowerInvariant() + "/" + manifest.Version;
            var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (_seen.TryGetValue(identity, out var previous))
            {
                if (manifest.Revision < previous.Revision)
                {
                    return "сайт повторил старое изменение выпуска";
                }

                if (manifest.Revision == previous.Revision && hash != previous.Sha256)
                {
                    return "один номер изменения выпуска содержит разные манифесты";
                }
            }

            if (previous is null || manifest.Revision > previous.Revision)
            {
                _seen[identity] = new Seen(manifest.Revision, hash);
                _dirty = true;
            }

            if (_path is null || !_dirty)
            {
                return null;
            }

            var temporary = _path + ".tmp";
            try
            {
                var content = JsonSerializer.SerializeToUtf8Bytes(_seen);
                if (content.Length > MaxBytes)
                {
                    return "история изменений выпусков переполнена — обновление не проверено";
                }

                using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    file.Write(content);
                    file.Flush(flushToDisk: true);
                }

                File.Move(temporary, _path, overwrite: true);
                _dirty = false;
                return null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Новый номер остается в памяти, но обновление не разрешается до записи; следующая проверка повторит ее.
                return "не удалось сохранить изменение выпуска — обновление не проверено";
            }
            finally
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Незавершенная временная запись не заменяет последнюю подтвержденную историю.
                }
            }
        }
    }

    private static bool Valid(string identity, Seen? seen)
    {
        var separator = identity.IndexOf('/');
        return separator > 0 && identity[..separator] is "stable" or "beta" && SemVer.IsValid(identity[(separator + 1)..])
            && seen is { Revision: >= 0 and <= UpdateManifest.MaxRevision, Sha256.Length: 64 } && seen.Sha256.All(char.IsAsciiHexDigitLower);
    }

    private sealed record Seen(long Revision, string Sha256);
}
