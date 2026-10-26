using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PrivateGateway.Client.Contracts;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Service.Updates;
using V1 = PrivateGateway.Client.Contracts.V1;

namespace PrivateGateway.Client.Service.Ipc;

/// <summary>
/// Где слушать и кого пускать. <paramref name="Endpoint"/> — путь к Unix-сокету или имя named pipe
/// (<see cref="IpcEndpoints"/>). Пользователь, установивший службу: <paramref name="OwnerUid"/> на
/// Unix, <paramref name="OwnerSid"/> на Windows; null — только тот, от чьего имени запущена служба.
/// root, администраторы и SYSTEM пускаются всегда. <paramref name="ServiceName"/> — имя службы
/// Windows, когда ее запускает диспетчер служб.
/// </summary>
public sealed record IpcOptions(string Endpoint, uint? OwnerUid = null, string? OwnerSid = null, string? ServiceName = null);

/// <summary>
/// Сервер IPC службы: gRPC по HTTP/2 без TLS — канал локальный, в сеть служба не слушает. Unix-сокет
/// — права 0600/0666 и проверка uid собеседника на каждом подключении (<see cref="PeerCredentials"/>);
/// named pipe — список доступа: пользователь, администраторы и SYSTEM, удаленный доступ по сети
/// запрещен.
/// </summary>
public static class IpcServer
{
    public static WebApplication Build(IpcOptions options, Gateway gateway, ServiceJournal journal, V1.ServiceInfo info, UpdateChecker? updates = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Файлов настроек у службы нет — и следить не за чем. По умолчанию ASP.NET Core следит за appsettings в корне
        // содержимого — текущей папке, а у службы systemd это «/»: inotify обходил бы всю файловую систему, запуск шел
        // десятки секунд и забирал у машины весь лимит слежения (ломая его другим программам и самому systemd).
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            Args = ["--hostBuilder:reloadConfigOnChange=false"],
        });
        builder.Logging.ClearProviders();

        // Запущена диспетчером служб Windows — живет по его командам; иначе ничего не меняется.
        builder.Host.UseWindowsService(o => o.ServiceName = options.ServiceName ?? "PrivateGateway");
        var pipe = IpcEndpoints.IsPipe(options.Endpoint);
        if (pipe && OperatingSystem.IsWindows())
        {
            var access = PipeAccess(options.OwnerSid);
            builder.WebHost.UseNamedPipes(o =>
            {
                o.CurrentUserOnly = false;
                o.PipeSecurity = access;
            });
        }

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            void Configure(ListenOptions listen)
            {
                listen.Protocols = HttpProtocols.Http2;
                if (!pipe)
                {
                    listen.Use(next => connection => Allowed(connection, options, journal) ? next(connection) : Refuse(connection));
                }
            }

            if (pipe)
            {
                kestrel.ListenNamedPipe(options.Endpoint, Configure);
            }
            else
            {
                PrepareSocket(options.Endpoint);
                kestrel.ListenUnixSocket(options.Endpoint, Configure);
            }
        });
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(gateway);
        builder.Services.AddSingleton(journal);
        builder.Services.AddSingleton(info);
        // Проверки обновлений нет (тесты) — служба без ключей подписи: «нет подписанных выпусков», в сеть не ходит.
        builder.Services.AddSingleton(updates ?? UpdateChecker.WithoutKeys(journal));
        var app = builder.Build();
        app.MapGrpcService<GatewayRpc>();
        if (!pipe)
        {
            app.Lifetime.ApplicationStarted.Register(() => OpenSocket(options));
        }

        return app;
    }

    /// <summary>Пустить, если uid собеседника известен и это root, сама служба или владелец.</summary>
    public static bool IsAllowed(uint? peer, uint self, uint? owner) => peer is { } uid && (uid == 0 || uid == self || uid == owner);

    private static bool Allowed(ConnectionContext connection, IpcOptions options, ServiceJournal journal)
    {
        var uid = connection.Features.Get<IConnectionSocketFeature>()?.Socket is { } socket ? PeerCredentials.UnixUid(socket) : null;
        if (IsAllowed(uid, PeerCredentials.CurrentUid(), options.OwnerUid))
        {
            return true;
        }

        journal.Add(JournalLevel.Warning, "service", "IPC: отказ в подключении, uid " + (uid?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "неизвестен"));
        return false;
    }

    private static Task Refuse(ConnectionContext connection)
    {
        connection.Abort();
        return Task.CompletedTask;
    }

    /// <summary>Папка сокета — только для своего владельца (служба пользователя) или для всех на чтение (системная); старый сокет удаляется.</summary>
    private static void PrepareSocket(string path)
    {
        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(folder, PeerCredentials.CurrentUid() == 0
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute
                : UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// Системная служба открывает сокет всем на запись — пускает дальше проверка uid; служба
    /// пользователя оставляет его только себе.
    /// </summary>
    private static void OpenSocket(IpcOptions options)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(options.Endpoint))
        {
            File.SetUnixFileMode(options.Endpoint, PeerCredentials.CurrentUid() == 0
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite
                    | UnixFileMode.OtherRead | UnixFileMode.OtherWrite
                : UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>Pipe: текущий пользователь, владелец (установивший службу), администраторы и SYSTEM — да; вход по сети (SMB) — нет.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static PipeSecurity PipeAccess(string? owner)
    {
        var security = new PipeSecurity();
        var rights = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, rights | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), rights, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), rights, AccessControlType.Allow));
        if (owner is not null)
        {
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(owner), rights, AccessControlType.Allow));
        }

        return security;
    }
}
