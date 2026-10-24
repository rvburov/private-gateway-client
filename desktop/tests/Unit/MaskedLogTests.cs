using Microsoft.Extensions.Logging;
using PrivateGateway.Client.Infrastructure.Logging;
using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Tests.Unit;

public sealed class MaskedLogTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "pg-log-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        if (Directory.Exists(_paths.DataDirectory))
        {
            Directory.Delete(_paths.DataDirectory, recursive: true);
        }
    }

    [Fact]
    public void Secrets_never_reach_the_log_file_even_inside_exceptions()
    {
        const string link = "vless://11111111-2222-3333-4444-555555555555@203.0.113.24:443?pbk=AbCd";
        const string subscription = "https://example.com/sub/T0kEn123secret";
        using (var factory = LoggingSetup.CreateFactory(_paths))
        {
            var logger = factory.CreateLogger("Тест");
#pragma warning disable CA1848, CA1873, CA2254 // в тесте важен текст строки, а не быстрый путь журнала
            logger.LogInformation("Импорт {Link} из {Url}", link, subscription);
            logger.LogError(new InvalidOperationException("не открыть " + subscription), "Сбой");
#pragma warning restore CA1848, CA1873, CA2254
        }

        var text = string.Concat(Directory.GetFiles(_paths.LogDirectory).Select(File.ReadAllText));
        text.ShouldContain("Импорт vless://[скрыто] из https://example.com/[скрыто]");
        text.ShouldContain("InvalidOperationException");
        text.ShouldNotContain("11111111-2222");
        text.ShouldNotContain("T0kEn123secret");
    }
}
