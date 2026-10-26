using System.Diagnostics;
using System.Threading.Channels;
using PrivateGateway.Client.Application.Notifications;
using PrivateGateway.Client.Infrastructure.Notifications;
using Tmds.DBus.Protocol;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Уведомления Linux по настоящей шине D-Bus: своя шина сеанса (dbus-daemon) и поддельный сервер уведомлений
/// org.freedesktop.Notifications. Без dbus-daemon (Windows, macOS) тесты пропускаются.
/// </summary>
public sealed class FreedesktopNotifierTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly NoticeText Lost = new("Соединение потеряно", "Интернет заблокирован, пока VPN не вернется. Переподключаюсь к «A & B»…",
        [new(NoticeAction.ChangeServer, "Сменить сервер"), new(NoticeAction.Open, "Открыть")]);

    [Fact]
    public async Task Notice_goes_with_buttons_urgency_and_sound_and_replaces_the_previous_one()
    {
        await using var bus = await SessionBus.StartAsync();
        await using var server = await FakeNotificationServer.StartAsync(bus.Address, ["body", "actions", "body-markup"]);
        using var notifier = new FreedesktopNotifier(bus.Address);

        await notifier.ShowAsync(Notice.ProtectionTag, Lost, important: true, Ct);

        var first = server.Calls.ShouldHaveSingleItem();
        (first.App, first.Replaces, first.Icon, first.Summary).ShouldBe(("Private Gateway", 0u, "network-vpn", "Соединение потеряно"));
        first.Body.ShouldBe("Интернет заблокирован, пока VPN не вернется. Переподключаюсь к «A &amp; B»…", "сервер с разметкой получает текст, а не разметку");
        first.Actions.ShouldBe(["default", "Соединение потеряно", "ChangeServer", "Сменить сервер", "Open", "Открыть"]);
        first.Urgency.ShouldBe((byte)1, "важное — «обычная» срочность, не «критическая»: «Не беспокоить» соблюдается");
        (first.SuppressSound, first.HasSound).ShouldBe((false, true), "у важного — звук");

        await notifier.ShowAsync(Notice.ProtectionTag, Lost with { Buttons = [] }, important: false, Ct);

        var second = server.Calls[^1];
        second.Replaces.ShouldBe(first.Id, "уведомление о защите одно: новое заменяет прежнее");
        (second.Urgency, second.SuppressSound, second.HasSound).ShouldBe(((byte)0, true, false), "обычное — без звука");

        await notifier.ShowAsync("servers-Основная", Lost, important: false, Ct);
        server.Calls[^1].Replaces.ShouldBe(0u, "о подписке — свое уведомление");
    }

    [Fact]
    public async Task Pressed_button_and_the_notice_itself_come_back_with_the_tag()
    {
        await using var bus = await SessionBus.StartAsync();
        await using var server = await FakeNotificationServer.StartAsync(bus.Address, ["body", "actions"]);
        using var notifier = new FreedesktopNotifier(bus.Address);
        var replies = Channel.CreateUnbounded<NoticeReply>();
        notifier.Replied += (_, reply) => replies.Writer.TryWrite(reply);
        await notifier.ShowAsync(Notice.ProtectionTag, Lost, important: true, Ct);
        var id = server.Calls[^1].Id;

        server.Emit("ActionInvoked", id + 100, "Open");
        server.Emit("ActionInvoked", id, "ChangeServer");
        (await NextAsync(replies)).ShouldBe(new NoticeReply(Notice.ProtectionTag, NoticeAction.ChangeServer), "чужое уведомление не наше");

        server.Emit("ActionInvoked", id, "default");
        (await NextAsync(replies)).ShouldBe(new NoticeReply(Notice.ProtectionTag, null), "нажали само уведомление — открыть окно");

        await notifier.CloseAsync(Notice.ProtectionTag, Ct);
        server.Closed.ShouldBe([id], "беда прошла — уведомление убрано");
        await notifier.CloseAsync(Notice.ProtectionTag, Ct);
        server.Closed.Count.ShouldBe(1, "второй раз убирать нечего");
    }

    [Fact]
    public async Task Server_without_buttons_gets_only_the_click_and_plain_text()
    {
        await using var bus = await SessionBus.StartAsync();
        await using var server = await FakeNotificationServer.StartAsync(bus.Address, ["body"]);
        using var notifier = new FreedesktopNotifier(bus.Address);

        await notifier.ShowAsync(Notice.ProtectionTag, Lost, important: true, Ct);

        var call = server.Calls.ShouldHaveSingleItem();
        call.Actions.ShouldBe(["default", "Соединение потеряно"], "кнопок сервер не умеет — нажатие открывает окно");
        call.Body.ShouldContain("«A & B»", Case.Sensitive, "без разметки — текст как есть");
    }

    [Fact]
    public async Task Missing_notification_server_or_bus_is_silent()
    {
        await using var bus = await SessionBus.StartAsync();
        using var notifier = new FreedesktopNotifier(bus.Address);

        await notifier.ShowAsync(Notice.ProtectionTag, Lost, important: true, Ct);
        await notifier.CloseAsync(Notice.ProtectionTag, Ct);

        using var nowhere = new FreedesktopNotifier("unix:path=/nonexistent/private-gateway-bus");
        await nowhere.ShowAsync(Notice.ProtectionTag, Lost, important: true, Ct);

        await using var server = await FakeNotificationServer.StartAsync(bus.Address, ["body"]);
        await notifier.ShowAsync(Notice.ProtectionTag, Lost, important: true, Ct);
        server.Calls.ShouldHaveSingleItem("сервер уведомлений появился — следующее уведомление дошло");
    }

    // Ждать только асинхронно: обработчики D-Bus идут через контекст тестов, блокирующее ожидание его занимает.
    private static async Task<NoticeReply> NextAsync(Channel<NoticeReply> replies)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await replies.Reader.ReadAsync(timeout.Token);
    }

    /// <summary>Своя шина сеанса: dbus-daemon печатает адрес, когда готов.</summary>
    private sealed class SessionBus : IAsyncDisposable
    {
        private readonly Process _daemon;

        private SessionBus(Process daemon, string address)
        {
            _daemon = daemon;
            Address = address;
        }

        public string Address { get; }

        public static async Task<SessionBus> StartAsync()
        {
            if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/dbus-daemon"))
            {
                Assert.Skip("нужен dbus-daemon (Linux)");
            }

            var daemon = Process.Start(new ProcessStartInfo("/usr/bin/dbus-daemon", "--session --nofork --print-address")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("dbus-daemon не запустился");
            var address = await daemon.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken);
            return new SessionBus(daemon, address ?? throw new InvalidOperationException("dbus-daemon не назвал адрес"));
        }

        public async ValueTask DisposeAsync()
        {
            _daemon.Kill();
            await _daemon.WaitForExitAsync();
            _daemon.Dispose();
        }
    }

    private sealed record NotifyCall(uint Id, string App, uint Replaces, string Icon, string Summary, string Body, string[] Actions, byte Urgency,
        bool SuppressSound, bool HasSound);

    /// <summary>Сервер уведомлений, который все запоминает и умеет прислать сигнал.</summary>
    private sealed class FakeNotificationServer : IPathMethodHandler, IAsyncDisposable
    {
        private const string Bus = "org.freedesktop.Notifications";
        private readonly DBusConnection _connection;
        private readonly string[] _capabilities;
        private uint _next;

        private FakeNotificationServer(DBusConnection connection, string[] capabilities)
        {
            _connection = connection;
            _capabilities = capabilities;
        }

        public List<NotifyCall> Calls { get; } = [];

        public List<uint> Closed { get; } = [];

        public string Path => "/org/freedesktop/Notifications";

        public bool HandlesChildPaths => false;

        public static async Task<FakeNotificationServer> StartAsync(string address, string[] capabilities)
        {
            var connection = new DBusConnection(address);
            await connection.ConnectAsync();
            var server = new FakeNotificationServer(connection, capabilities);
            connection.AddMethodHandler(server);
            await connection.RequestNameAsync(Bus, RequestNameOptions.Default);
            return server;
        }

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var request = context.Request;
            switch (request.MemberAsString)
            {
                case "GetCapabilities":
                {
                    using var writer = context.CreateReplyWriter("as");
                    writer.WriteArray(_capabilities);
                    context.Reply(writer.CreateMessage());
                    break;
                }

                case "Notify":
                {
                    var reader = request.GetBodyReader();
                    var (app, replaces, icon, summary, body) = (reader.ReadString(), reader.ReadUInt32(), reader.ReadString(), reader.ReadString(), reader.ReadString());
                    var actions = reader.ReadArrayOfString();
                    var hints = reader.ReadDictionaryOfStringToVariantValue();
                    var id = replaces != 0 ? replaces : ++_next;
                    lock (Calls)
                    {
                        Calls.Add(new NotifyCall(id, app, replaces, icon, summary, body, actions, hints["urgency"].GetByte(),
                            hints.TryGetValue("suppress-sound", out var quiet) && quiet.GetBool(), hints.ContainsKey("sound-name")));
                    }

                    using var writer = context.CreateReplyWriter("u");
                    writer.WriteUInt32(id);
                    context.Reply(writer.CreateMessage());
                    break;
                }

                case "CloseNotification":
                {
                    Closed.Add(request.GetBodyReader().ReadUInt32());
                    using var writer = context.CreateReplyWriter(null);
                    context.Reply(writer.CreateMessage());
                    break;
                }

                default:
                    context.ReplyUnknownMethodError();
                    break;
            }

            return ValueTask.CompletedTask;
        }

        public void Emit(string signal, uint id, string action)
        {
            using var writer = _connection.GetMessageWriter();
            writer.WriteSignalHeader(null, Path, Bus, signal, "us");
            writer.WriteUInt32(id);
            writer.WriteString(action);
            _connection.TrySendMessage(writer.CreateMessage());
        }

        public ValueTask DisposeAsync()
        {
            _connection.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
