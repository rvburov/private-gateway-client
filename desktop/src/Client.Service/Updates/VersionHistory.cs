using PrivateGateway.Client.Core;

namespace PrivateGateway.Client.Service.Updates;

/// <summary>Какая версия стояла до нынешней (на нее можно откатиться) и с какой откатились (ее больше не предлагать).</summary>
public sealed record VersionHistory(string? Previous, string? Left)
{
    /// <summary>Версия последнего запуска службы.</summary>
    public const string CurrentFile = "version";

    /// <summary>Прежняя, более старая версия — после обновления.</summary>
    public const string PreviousFile = "previous-version";

    /// <summary>Более новая версия, с которой откатились.</summary>
    public const string LeftFile = "left-version";

    /// <summary>
    /// Запуск службы версии <paramref name="current"/>: в прошлый раз работала другая — значит, программу обновили
    /// (прежняя старше — ее можно вернуть) или откатили (прежняя новее — ее больше не предлагать). Та же версия —
    /// история не меняется. Файлы — в папке состояния службы, пишет только служба.
    /// </summary>
    public static VersionHistory Record(string state, string current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var last = Read(state, CurrentFile);
        if (last is not null && last != current)
        {
            var older = SemVer.Compare(last, current) < 0;
            Write(state, older ? PreviousFile : LeftFile, last);
            Delete(state, older ? LeftFile : PreviousFile);
        }

        Write(state, CurrentFile, current);
        return new VersionHistory(Read(state, PreviousFile), Read(state, LeftFile));
    }

    private static string? Read(string state, string file)
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(state, file)).Trim();
            return SemVer.IsValid(text) ? text : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Write(string state, string file, string version)
    {
        try
        {
            File.WriteAllText(Path.Combine(state, file), version);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Не записалось — откат в этот раз недоступен, служба работает.
        }
    }

    private static void Delete(string state, string file)
    {
        try
        {
            File.Delete(Path.Combine(state, file));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Останется — поправит следующий запуск.
        }
    }
}
