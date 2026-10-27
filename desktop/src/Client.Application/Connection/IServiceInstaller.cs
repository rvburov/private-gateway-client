namespace PrivateGateway.Client.Application.Connection;

/// <summary>Чем кончилась установка службы.</summary>
public enum ServiceInstallResult
{
    /// <summary>Служба установлена и отвечает.</summary>
    Installed,

    /// <summary>Пользователь не дал прав (закрыл запрос ОС).</summary>
    Cancelled,

    /// <summary>Установка не вышла или служба не ответила.</summary>
    Failed,
}

/// <summary>
/// «Установить службу» (доска 16, первый запуск): окно просит у ОС права и запускает установку
/// службы (<c>--install</c>) от администратора или root. Сама установка — в службе.
/// </summary>
public interface IServiceInstaller
{
    /// <summary>Установка есть на этой ОС и файл службы рядом с программой.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Сборка службы, которую поставит установка, — коммит из версии ее файла (<see cref="Core.SemVer.Build"/>); пусто —
    /// файл без коммита; null — не узнать (файла нет, выпуск одним файлом).
    /// </summary>
    string? SourceBuild => null;

    Task<ServiceInstallResult> InstallAsync(CancellationToken cancellationToken = default);
}
