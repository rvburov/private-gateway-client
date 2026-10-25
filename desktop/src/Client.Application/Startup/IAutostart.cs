namespace PrivateGateway.Client.Application.Startup;

/// <summary>Запуск вместе с ОС: запись в автозагрузку пользователя (реестр, LaunchAgent, XDG autostart).</summary>
public interface IAutostart
{
    /// <summary>
    /// Можно ли записать: программа установлена, а не запущена из исходников через dotnet — такую
    /// запись ОС запустить не сможет.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>Записать или убрать запуск вместе с ОС — с флагом «в трей», без окна.</summary>
    void Apply(bool enabled);
}
