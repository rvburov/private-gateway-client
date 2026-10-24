using System.Reflection;

namespace PrivateGateway.Client.Service;

/// <summary>Версия службы из сборки (Version в Directory.Build.props).</summary>
public static class ServiceVersion
{
    public static string Current { get; } =
        typeof(ServiceVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "0.0.0";
}
