namespace PrivateGateway.Client.Core;

/// <summary>Что служба говорит о себе (GetServiceInfo): своя версия, версия контракта IPC и ядра.</summary>
/// <param name="Version">Версия службы (SemVer, без метаданных сборки).</param>
/// <param name="ApiVersion">Версия контракта IPC: «1».</param>
/// <param name="Cores">Ядра, которые служба нашла рядом с собой.</param>
/// <param name="Build">Сборка службы — коммит исходников (<see cref="SemVer.Build"/>); неизвестна — пусто.</param>
public sealed record ServiceDetails(string Version, string ApiVersion, IReadOnlyList<CoreDetails> Cores, string Build = "")
{
    /// <summary>Ядро по имени (xray, sing-box), если оно есть и запускается.</summary>
    public CoreDetails? Core(string name) => Cores.FirstOrDefault(c => c.Name == name && c.Available);
}

/// <summary>Ядро службы: имя, версия из его собственного вывода, запускается ли.</summary>
public sealed record CoreDetails(string Name, string Version, bool Available);
