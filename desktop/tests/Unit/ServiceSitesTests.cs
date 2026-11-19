using PrivateGateway.Client.Core.Settings;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// «Все адреса сервиса» (решение 741): список сервисов — ресурс сборки из shared/specifications/routing/services.json; домены
/// взяты из того же коммита domain-list-community, что geosite.dat (geosite.version), и годятся в шаблон правила.
/// </summary>
public sealed class ServiceSitesTests
{
    [Theory]
    [InlineData("youtube.com", "youtube")]
    [InlineData("m.youtube.com", "youtube")]
    [InlineData("googlevideo.com", "youtube")]
    [InlineData("t.me", "telegram")]
    [InlineData("chatgpt.com", "openai")]
    [InlineData("example.com", null)]
    [InlineData("notyoutube.com", null)]
    [InlineData("*.youtube.com", null)]
    [InlineData("geosite:youtube", null)]
    [InlineData("203.0.113.9", null)]
    public void Plain_domain_finds_its_service(string pattern, string? id)
    {
        ServiceSites.Find(pattern)?.Id.ShouldBe(id);
        if (id is null)
        {
            ServiceSites.Find(pattern).ShouldBeNull();
        }
    }

    [Fact]
    public void Rule_of_a_service_is_its_geosite_category()
    {
        var youtube = ServiceSites.OfPattern("geosite:youtube").ShouldNotBeNull();
        (youtube.Name, youtube.Pattern).ShouldBe(("YouTube", "geosite:youtube"));
        ServiceSites.OfPattern("geosite:category-ads-all").ShouldBeNull("не из списка сервисов");
        ServiceSites.OfPattern("youtube.com").ShouldBeNull();
        youtube.Contains("yt3.googleusercontent.com").ShouldBeTrue("точное имя");
        youtube.Contains("x.yt3.googleusercontent.com").ShouldBeFalse("у точного имени поддоменов нет");
    }

    [Fact]
    public void List_comes_from_the_geosite_commit_and_holds_valid_names()
    {
        using var file = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(FindRoot(), "shared", "specifications", "routing", "services.json")));
        file.RootElement.GetProperty("commit").GetString()
            .ShouldBe(File.ReadAllText(Path.Combine(FindRoot(), "shared", "cores", "geosite.version")).Trim(), "списки — из той же базы, что geosite.dat");
        ServiceSites.All.Count.ShouldBe(file.RootElement.GetProperty("services").GetArrayLength());
        ServiceSites.All.Select(s => s.Id).ShouldBeUnique();
        foreach (var service in ServiceSites.All)
        {
            service.Domains.ShouldNotBeEmpty(service.Id);
            service.Domains.Concat(service.Full).ShouldAllBe(d => SitePattern.Normalize(d) == d && SitePattern.IsPlainDomain(d), service.Id);
            SitePattern.Normalize(service.Pattern).ShouldBe(service.Pattern);
        }
    }

    private static string FindRoot()
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
