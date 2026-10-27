using System.Reflection;
using PrivateGateway.Client.Core;

namespace PrivateGateway.Client.Service;

/// <summary>Версия службы из сборки (Version в Directory.Build.props) и ее сборка — коммит исходников.</summary>
public static class ServiceVersion
{
    private static readonly string? Informational =
        typeof(ServiceVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    public static string Current { get; } = Informational?.Split('+')[0] ?? "0.0.0";

    /// <summary>Коммит исходников, из которых собрана служба (<see cref="SemVer.Build"/>); не из git — пусто.</summary>
    public static string Build { get; } = SemVer.Build(Informational);
}
