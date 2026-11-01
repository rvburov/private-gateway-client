using System.Net;

namespace PrivateGateway.Client.Tests.Network;

public sealed class CoreOutputTests
{
    [Fact]
    public async Task Http_failure_contains_the_protocol_core_output_and_original_exception()
    {
        using var site = new LocalSite("отказ", status: "503 Service Unavailable");
        using var handler = new SocketsHttpHandler { UseProxy = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        var output = new CoreOutput();
        output.Add("xray", "taking detour [block] for [tcp:198.18.0.1:80]");
        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            output.TraceAsync(() => http.GetStringAsync(site.Url, TestContext.Current.CancellationToken), "http к fake IP"));
        error.Message.ShouldContain("http к fake IP");
        error.Message.ShouldContain("xray: taking detour [block]");
        error.InnerException.ShouldBeOfType<HttpRequestException>().StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }
}
