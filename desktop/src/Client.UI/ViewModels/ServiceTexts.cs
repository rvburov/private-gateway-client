using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.UI.Localization;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>Как окно называет службу, которая ему отвечает: «работает», «служба пользователя», «не найдена», «имитация».</summary>
public static class ServiceTexts
{
    public static string State(ServiceKind kind) => Loc.Instance["service.kind." + kind.ToString().ToLowerInvariant()];
}
