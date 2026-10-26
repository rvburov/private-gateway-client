using System.Xml.Linq;
using PrivateGateway.Client.Application.Notifications;
using PrivateGateway.Client.Infrastructure.Notifications;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Разметка уведомления Windows и разбор нажатия: на всех ОС, без Windows.</summary>
public class ToastXmlTests
{
    private static readonly NoticeText Gone = new("Сервер исчез из подписки", "«A & <B>» больше нет в «Основной».",
        [new(NoticeAction.ChooseOther, "Выбрать другой"), new(NoticeAction.Later, "Позже"), new(NoticeAction.Open, "Лишняя")]);

    [Fact]
    public void Toast_has_title_text_two_buttons_and_sound_only_when_important()
    {
        var tag = "servers-Основная & Резерв";
        var toast = XElement.Parse(ToastXml.Content(tag, Gone, important: false));

        toast.Descendants("text").Select(t => t.Value).ShouldBe(["Сервер исчез из подписки", "«A & <B>» больше нет в «Основной»."]);
        var buttons = toast.Descendants("action").ToList();
        buttons.Select(b => (string?)b.Attribute("content")).ShouldBe(["Выбрать другой", "Позже"], "не больше двух кнопок (доска 22)");
        buttons.ShouldAllBe(b => (string?)b.Attribute("activationType") == "foreground");
        ((string?)toast.Element("audio")!.Attribute("silent")).ShouldBe("true", "обычное — без звука");

        ToastXml.Reply((string)buttons[0].Attribute("arguments")!).ShouldBe(new NoticeReply(tag, NoticeAction.ChooseOther));
        ToastXml.Reply((string)toast.Attribute("launch")!).ShouldBe(new NoticeReply(tag, null), "нажали само уведомление");

        var loud = XElement.Parse(ToastXml.Content(Notice.ProtectionTag, Gone with { Buttons = [] }, important: true));
        loud.Element("actions").ShouldBeNull();
        loud.Element("audio")!.Attribute("silent").ShouldBeNull("важное — со звуком");
    }

    [Fact]
    public void Foreign_arguments_are_not_a_reply()
    {
        ToastXml.Reply("").ShouldBeNull();
        ToastXml.Reply("action=Open").ShouldBeNull("без метки — не наше");
        ToastXml.Reply("action=Explode&tag=protection").ShouldBe(new NoticeReply(Notice.ProtectionTag, null), "неизвестная кнопка — как само уведомление");
    }
}
