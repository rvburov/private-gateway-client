using PrivateGateway.Client.UI.Localization;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>Прогресс по завершенным этапам установки службы, общий для трех экранов.</summary>
public static class ServiceSetupProgress
{
    public static string Text(int percent) => Loc.Instance.Format("welcome.service.progress", percent,
        Loc.Instance[percent switch
        {
            < 10 => "welcome.service.stage.rights",
            < 30 => "welcome.service.stage.check",
            < 60 => "welcome.service.stage.copy",
            < 80 => "welcome.service.stage.register",
            < 90 => "welcome.service.stage.start",
            < 100 => "welcome.service.stage.answer",
            _ => "welcome.service.done",
        }]);
}
