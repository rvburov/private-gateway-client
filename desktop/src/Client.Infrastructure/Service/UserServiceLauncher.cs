using System.Diagnostics;
using System.Globalization;
using PrivateGateway.Client.Contracts;

namespace PrivateGateway.Client.Infrastructure.Service;

/// <summary>Запуск службы пользователя — режим прокси без установленной службы.</summary>
public interface IUserServiceLauncher
{
    /// <summary>Запустить службу; false — запускать нечего (файла службы нет рядом с программой).</summary>
    bool Start();
}

/// <summary>
/// Служба пользователя — тот же исполняемый файл службы с <c>--user</c>: без прав, только режим
/// прокси, свой адрес IPC. С <c>--parent</c> — номером процесса окна: закроется окно — остановится и
/// она, прокси без окна некому выключить.
/// </summary>
/// <param name="executable">Файл службы; null — его нет.</param>
/// <param name="parentId">Процесс окна.</param>
/// <param name="endpoint">Свой адрес IPC (тесты); null — адрес службы пользователя этой ОС.</param>
/// <param name="state">Своя папка состояния (тесты); null — папка пользователя (<see cref="UserServiceFiles"/>).</param>
public sealed class UserServiceLauncher(string? executable, int parentId, string? endpoint = null, string? state = null) : IUserServiceLauncher
{
    /// <summary>Имя исполняемого файла службы на этой ОС.</summary>
    public static string FileName => OperatingSystem.IsWindows() ? "PrivateGateway.Service.exe" : "PrivateGateway.Service";

    /// <summary>Служба этого окна: файл рядом с программой, процесс окна, адрес по умолчанию.</summary>
    public static UserServiceLauncher ForThisWindow() => new(Find(AppContext.BaseDirectory), Environment.ProcessId);

    /// <summary>
    /// Файл службы рядом с программой (так кладет установщик); программа запущена из папки сборки
    /// (artifacts/bin/Client.UI/&lt;конфигурация&gt;) — из соседней artifacts/bin/Client.Service/&lt;конфигурация&gt;.
    /// </summary>
    /// <param name="windowFolder">Папка исполняемого файла окна.</param>
    public static string? Find(string windowFolder)
    {
        var installed = Path.Combine(windowFolder, FileName);
        if (File.Exists(installed))
        {
            return installed;
        }

        // Только точное расположение папки сборки, без поиска вверх: чужой файл выше по дереву службой не станет.
        var build = new DirectoryInfo(windowFolder);
        return build.Parent is { Name: "Client.UI" } project && project.Parent is { Name: "bin" } bin && bin.Parent is { Name: "artifacts" }
            && Path.Combine(bin.FullName, "Client.Service", build.Name, FileName) is var built && File.Exists(built)
            ? built
            : null;
    }

    public bool Start()
    {
        if (executable is null || !File.Exists(executable))
        {
            return false;
        }

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            // Служба — консольная программа: без окна консоли на Windows.
            CreateNoWindow = true,
            ArgumentList = { "--user", "--parent", parentId.ToString(CultureInfo.InvariantCulture) },
        };
        if (endpoint is not null)
        {
            start.ArgumentList.Add("--ipc");
            start.ArgumentList.Add(endpoint);
        }

        if (state is not null)
        {
            start.ArgumentList.Add("--state");
            start.ArgumentList.Add(state);
        }

        try
        {
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
