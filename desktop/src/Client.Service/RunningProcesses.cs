using System.Diagnostics;
using PrivateGateway.Client.Core.Platform;

namespace PrivateGateway.Client.Service;

/// <summary>
/// Процессы машины для поиска других программ VPN и прокси (<see cref="OtherClients"/>): имя у каждого, путь к файлу — только у
/// известных программ (их единицы, а путь читается дольше имени). Служба работает от имени системы и видит все процессы;
/// процесс, путь которого ОС не дает (защищенный, уже вышел), — без пути: своим ядром его не признать.
/// </summary>
public static class RunningProcesses
{
    public static IReadOnlyList<RunningProcess> Read()
    {
        var result = new List<RunningProcess>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var name = process.ProcessName;
                    result.Add(new RunningProcess(process.Id, name, OtherClients.IsKnown(name) ? Path(process) : null));
                }
                catch (InvalidOperationException)
                {
                    // Процесс вышел, пока шел обход.
                }
            }
        }

        return result;
    }

    private static string? Path(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
