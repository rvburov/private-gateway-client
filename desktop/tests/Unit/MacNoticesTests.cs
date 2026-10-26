using PrivateGateway.Client.Application.Notifications;
using PrivateGateway.Client.Infrastructure.Notifications;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// Уведомления macOS без ОС: категории по набору кнопок, ответ на нажатие (само уведомление, кнопка, смахнули, чужое),
/// уровень вмешательства. Сам центр уведомлений проверяет смоук пакета macOS (--check-notifications из .app).
/// </summary>
public sealed class MacNoticesTests
{
    [Fact]
    public void Same_buttons_share_a_category_and_no_buttons_need_none()
    {
        NoticeButton[] retry = [new(NoticeAction.Retry, "Повторить"), new(NoticeAction.ChooseOther, "Выбрать другой")];

        MacNotices.Category(retry).ShouldBe("pg.buttons.Retry.ChooseOther");
        MacNotices.Category([new(NoticeAction.Retry, "Retry"), new(NoticeAction.ChooseOther, "Choose another")])
            .ShouldBe(MacNotices.Category(retry), "категория — по действиям, а не по подписям: язык не плодит категорий");
        MacNotices.Category([.. retry.Reverse()]).ShouldNotBe(MacNotices.Category(retry), "порядок кнопок — часть набора");
        MacNotices.Category([]).ShouldBeNull();
    }

    [Fact]
    public void Press_of_the_notice_or_its_button_comes_back_and_a_dismiss_does_not()
    {
        MacNotices.Reply("connection-lost", MacNotices.DefaultAction).ShouldBe(new NoticeReply("connection-lost", null), "само уведомление — открыть окно");

        foreach (var action in Enum.GetValues<NoticeAction>())
        {
            MacNotices.Reply("connection-lost", MacNotices.Action(action)).ShouldBe(new NoticeReply("connection-lost", action));
        }

        MacNotices.Reply("connection-lost", MacNotices.DismissAction).ShouldBeNull("смахнули — не нажатие");
        MacNotices.Reply("connection-lost", "pg.action.Format").ShouldBeNull("чужое действие");
        MacNotices.Reply("connection-lost", "pg.action.42").ShouldBeNull("число — не действие");
        MacNotices.Reply("connection-lost", "other.Retry").ShouldBeNull();
        MacNotices.Reply(null, MacNotices.DefaultAction).ShouldBeNull("без метки отвечать некому");
        MacNotices.Reply("connection-lost", null).ShouldBeNull();
    }

    [Fact]
    public void Important_notice_is_active_and_the_rest_is_passive() =>
        (MacNotices.InterruptionLevel(important: true), MacNotices.InterruptionLevel(important: false)).ShouldBe((MacNotices.Active, MacNotices.Passive));

    [Fact]
    public void Outside_an_app_bundle_there_is_no_mac_notifier()
    {
        if (!OperatingSystem.IsMacOS())
        {
            Assert.Skip("центр уведомлений macOS");
            return;
        }

        // Тесты идут не из пакета .app: центр уведомлений бросил бы исключение Objective-C — его не трогают.
        MacNotifier.ForThisApp().ShouldBeNull();
        SystemNotifiers.ForCurrentOs().IsSupported.ShouldBeFalse("вне пакета — без уведомлений, о беде говорит трей");
    }
}
