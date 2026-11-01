using System.Runtime.Versioning;
using Microsoft.Win32;
using PrivateGateway.Client.Infrastructure.Startup;

namespace PrivateGateway.Client.Infrastructure.Service;

/// <summary>
/// Запуск окна при следующем входе в Windows, пока окно держит прокси пользователя на 127.0.0.1, а автозапуск выключен
/// (решение 534). Прежние настройки прокси возвращает только окно: без этого, если служба отключится, пока окно закрыто
/// (перезагрузка, остановка службы), браузеры остались бы на неработающем порту — без интернета, пока программу не откроют.
/// </summary>
public interface IProxyLogonRecovery
{
    /// <summary>true — запуск при следующем входе нужен; false — нет, запись убирается.</summary>
    void Require(bool needed);
}

/// <summary>
/// Запись HKCU\Software\Microsoft\Windows\CurrentVersion\RunOnce: Windows запускает ее один раз при следующем входе и сама
/// удаляет. Окно стартует с <c>--minimized</c> — только значок в трее — и сверяет прокси с подключением: защита выключена —
/// возвращает прежние настройки, включена — оставляет и снова ставит запись, пока держит прокси.
/// </summary>
/// <param name="executable">Окно; null — запуск из исходников (dotnet), запускать нечего.</param>
/// <param name="entryName">Имя записи; в тестах — свое, чтобы не тронуть настоящую.</param>
public sealed class RunOnceProxyRecovery(string? executable, string entryName = RunOnceProxyRecovery.Name) : IProxyLogonRecovery
{
    public const string Name = "Private Gateway proxy";
    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";

    /// <summary>Для окна этого процесса: только Windows и только собранное окно, не dotnet и не testhost.</summary>
    public static RunOnceProxyRecovery? ForThisWindow() =>
        OperatingSystem.IsWindows() && OsAutostart.ForCurrentUser().IsSupported ? new RunOnceProxyRecovery(Environment.ProcessPath) : null;

    /// <summary>Строка запуска, как ее увидит Windows.</summary>
    public string? Command => executable is { Length: > 0 } path ? $"\"{path}\" {OsAutostart.MinimizedArgument}" : null;

    public void Require(bool needed)
    {
        if (OperatingSystem.IsWindows() && Command is { } command)
        {
            Write(needed ? command : null);
        }
    }

    [SupportedOSPlatform("windows")]
    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunOnceKey);
        return key?.GetValue(entryName) as string;
    }

    [SupportedOSPlatform("windows")]
    private void Write(string? command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunOnceKey, writable: true);
        if (command is null)
        {
            key.DeleteValue(entryName, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(entryName, command, RegistryValueKind.String);
        }
    }
}
