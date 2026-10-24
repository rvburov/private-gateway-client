using System.Reflection;
using System.Xml.Linq;

namespace PrivateGateway.Client.Tests.Architecture;

/// <summary>
/// Границы модулей из docs/architecture/overview.md. Правила читаются из .csproj (объявленные
/// ссылки) и из собранных сборок (что код на самом деле использует).
/// </summary>
public class ModuleBoundariesTests
{
    // Разрешенные прямые ссылки на наши проекты. Новый проект без строки здесь роняет тест:
    // его место в слоях решается сознательно.
    private static readonly Dictionary<string, string[]> AllowedReferences = new()
    {
        ["Client.Core"] = [],
        ["Client.Contracts"] = ["Client.Core"],
        ["Client.Network"] = ["Client.Core"],
        ["Client.Application"] = ["Client.Core", "Client.Contracts", "Client.Network"],
        ["Client.Infrastructure"] = ["Client.Core", "Client.Contracts", "Client.Application"],
        ["Client.Platform.Windows"] = ["Client.Core"],
        ["Client.Platform.Linux"] = ["Client.Core"],
        ["Client.Platform.MacOS"] = ["Client.Core"],
        ["Client.Service"] =
        [
            "Client.Core", "Client.Contracts", "Client.Network",
            "Client.Platform.Windows", "Client.Platform.Linux", "Client.Platform.MacOS",
        ],
        ["Client.UI"] = ["Client.Core", "Client.Contracts", "Client.Application", "Client.Infrastructure"],
    };

    // Слои без сети и запуска программ: должны собираться с запретом из BannedSymbols.NoNetwork.txt.
    private static readonly string[] NoNetworkProjects =
        ["Client.Core", "Client.Network", "Client.Application", "Client.UI"];

    // Сборки .NET, ссылка на которые означает сеть или запуск программ.
    private static readonly string[] NetworkAssemblies =
    [
        "System.Net.Http", "System.Net.Sockets", "System.Net.NameResolution", "System.Net.Ping",
        "System.Net.Requests", "System.Net.WebClient", "System.Net.WebSockets.Client",
        "System.Net.HttpListener", "System.Diagnostics.Process",
    ];

    private static readonly string SourceRoot = Path.Combine(FindRepositoryRoot(), "desktop", "src");

    [Fact]
    public void Every_source_project_has_a_rule()
    {
        var projects = Directory.GetDirectories(SourceRoot).Select(Path.GetFileName).Order().ToArray();
        projects.ShouldBe(AllowedReferences.Keys.Order().ToArray(), ignoreOrder: false);
    }

    [Theory]
    [MemberData(nameof(ProjectNames))]
    public void Project_references_only_allowed_layers(string project)
    {
        var actual = ProjectReferences(project);
        var forbidden = actual.Except(AllowedReferences[project]).ToArray();
        forbidden.ShouldBeEmpty($"{project} ссылается на запрещенные проекты");
    }

    [Fact]
    public void Nobody_references_the_service_or_the_ui()
    {
        foreach (var project in AllowedReferences.Keys)
        {
            var refs = ProjectReferences(project);
            refs.ShouldNotContain("Client.Service", project);
            refs.ShouldNotContain("Client.UI", project);
        }
    }

    [Theory]
    [MemberData(nameof(NoNetworkProjectNames))]
    public void No_network_layer_is_built_with_the_ban(string project)
    {
        var value = Csproj(project).Descendants("PgNoNetwork").Select(e => e.Value.Trim()).FirstOrDefault();
        value.ShouldBe("true", $"{project}: в .csproj нужен <PgNoNetwork>true</PgNoNetwork>");
    }

    [Theory]
    [InlineData("Client.Core")]
    [InlineData("Client.Network")]
    [InlineData("Client.Application")]
    public void No_network_assembly_does_not_use_network_or_processes(string project)
    {
        var assembly = Assembly.Load("PrivateGateway." + project);
        var used = assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        used.Intersect(NetworkAssemblies).ShouldBeEmpty($"{project} использует сеть или запуск программ");
    }

    public static TheoryData<string> ProjectNames() => new(AllowedReferences.Keys);

    public static TheoryData<string> NoNetworkProjectNames() => new(NoNetworkProjects);

    private static XDocument Csproj(string project) =>
        XDocument.Load(Path.Combine(SourceRoot, project, project + ".csproj"));

    private static string[] ProjectReferences(string project) =>
        Csproj(project).Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value.Replace('\\', '/')))
            .ToArray();

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PrivateGateway.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Не найден корень репозитория (PrivateGateway.slnx)");
    }
}
