using System.Globalization;

namespace PrivateGateway.Client.Service;

/// <summary>
/// Служба готова: IPC слушает. Файл <c>ready</c> в папке состояния — «версия номер-процесса»; его пишет служба после
/// запуска IPC и убирает при остановке, а установка (<see cref="ServiceSetup.Install(SetupOptions, string, string?, string?, IOsService, TimeSpan?)"/>)
/// ждет его от новой копии — так она узнает, что новая служба не просто запущена, а работает.
/// </summary>
public static class ServiceReady
{
    public const string FileName = "ready";

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(200);

    public static void Mark(string state, string version) =>
        File.WriteAllText(Path.Combine(state, FileName), version + " " + Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

    public static void Clear(string state)
    {
        try
        {
            File.Delete(Path.Combine(state, FileName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Не убрался — следующая установка уберет перед запуском.
        }
    }

    /// <summary>Дождаться готовности службы версии <paramref name="version"/>; нет за <paramref name="timeout"/> — false.</summary>
    public static bool Wait(string state, string version, TimeSpan timeout)
    {
        var path = Path.Combine(state, FileName);
        var deadline = DateTime.UtcNow + timeout;
        do
        {
            try
            {
                if (File.Exists(path) && File.ReadAllText(path).StartsWith(version + " ", StringComparison.Ordinal))
                {
                    return true;
                }
            }
            catch (IOException)
            {
                // Пишется прямо сейчас.
            }

            Thread.Sleep(Poll);
        }
        while (DateTime.UtcNow < deadline);

        return false;
    }
}
