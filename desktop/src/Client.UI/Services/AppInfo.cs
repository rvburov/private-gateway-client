using System.Reflection;
using System.Runtime.InteropServices;

namespace PrivateGateway.Client.UI.Services;

/// <summary>Сведения о программе для подписей: версия и имя текущей ОС.</summary>
public static class AppInfo
{
    /// <summary>
    /// Версия для шапки, подвала настроек, отчета проверки и сверки со службой — из сборки (Version в Directory.Build.props,
    /// у выпуска — из тега), без метаданных сборки.
    /// </summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>Имя ОС для подписей («Запуск вместе с Windows»): программа одна на три ОС.</summary>
    public static string OsName =>
        OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";

    /// <summary>
    /// ОС с версией для «О программе»: «Windows 11», «macOS 15.1», на Linux — название дистрибутива
    /// из /etc/os-release.
    /// </summary>
    public static string OsVersion
    {
        get
        {
            var version = Environment.OSVersion.Version;
            if (OperatingSystem.IsWindows())
            {
                // У Windows 11 номер версии тот же — 10.0, отличается сборка.
                return version.Build >= 22000 ? "Windows 11" : $"Windows {version.Major}";
            }

            if (OperatingSystem.IsMacOS())
            {
                return $"macOS {version.Major}.{version.Minor}";
            }

            try
            {
                return File.ReadLines("/etc/os-release")
                    .Where(l => l.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                    .Select(l => l["PRETTY_NAME=".Length..].Trim('"'))
                    .FirstOrDefault(n => n.Length > 0) ?? "Linux";
            }
            catch (IOException)
            {
                return "Linux";
            }
        }
    }

    /// <summary>Архитектура процессора: x64, arm64.</summary>
    public static string Architecture => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
}
