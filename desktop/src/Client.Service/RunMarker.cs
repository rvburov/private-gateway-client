namespace PrivateGateway.Client.Service;

/// <summary>
/// Признак работы службы — файл running в папке состояния: создается при запуске, удаляется при штатной остановке. Остался от
/// прошлого запуска — тот завершился аварийно (убит, упал, машину выключили без остановки службы).
/// </summary>
public sealed class RunMarker(string folder) : IDisposable
{
    private string Path => System.IO.Path.Combine(folder, "running");

    /// <summary>Отметить запуск; true — прошлый запуск завершился аварийно. Не записалось — false: признака нет, служба работает.</summary>
    public bool Start()
    {
        try
        {
            var crashed = File.Exists(Path);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return crashed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Штатная остановка — признак убрать.</summary>
    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Не удалился — следующий запуск сочтет остановку аварийной: лишняя строка в журнале, не беда.
        }
    }
}
