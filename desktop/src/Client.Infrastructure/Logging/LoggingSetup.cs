using Microsoft.Extensions.Logging;
using PrivateGateway.Client.Infrastructure.Storage;
using Serilog;

namespace PrivateGateway.Client.Infrastructure.Logging;

/// <summary>Журнал интерфейса: файл в папке данных, по дню на файл, 7 дней, до 10 МБ, без секретов.</summary>
public static class LoggingSetup
{
    public static ILoggerFactory CreateFactory(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureCreated();
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                new MaskingTextFormatter(),
                Path.Combine(paths.LogDirectory, "client-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10 * 1024 * 1024)
            .CreateLogger();
        return LoggerFactory.Create(builder => builder.AddSerilog(serilog, dispose: true));
    }
}
