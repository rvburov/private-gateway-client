namespace PrivateGateway.Client.Application.Connection;

/// <summary>Регистрация службы отдельно от связи с ней: разрешение ОС не доказывает, что процесс отвечает.</summary>
public enum ServiceAccess
{
    Unsupported,
    Unknown,
    NotInstalled,
    Installed,
    RequiresApproval,
}

/// <summary>Чтение разрешения фоновой работы и открытие настроек по нажатию пользователя. Службу и сеть не меняет.</summary>
public interface IServiceAccess
{
    bool IsSupported => false;
    ServiceAccess Read() => ServiceAccess.Unsupported;
    bool OpenSettings() => false;
}

/// <summary>Другая ОС или демо: отдельная проверка разрешения не применяется.</summary>
public sealed class NoServiceAccess : IServiceAccess;
