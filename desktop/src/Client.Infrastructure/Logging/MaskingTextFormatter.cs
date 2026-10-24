using PrivateGateway.Client.Core.Logging;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace PrivateGateway.Client.Infrastructure.Logging;

/// <summary>
/// Строка журнала целиком — с сообщением, свойствами и исключением — проходит через
/// <see cref="SecretMasker"/> перед записью: ключи, UUID, ссылки серверов и пути подписок в файл
/// не попадают, даже если их передали в исключении.
/// </summary>
public sealed class MaskingTextFormatter : ITextFormatter
{
    public const string Template =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}";

    private readonly MessageTemplateTextFormatter _inner = new(Template, System.Globalization.CultureInfo.InvariantCulture);

    public void Format(LogEvent logEvent, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        using var buffer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        _inner.Format(logEvent, buffer);
        output.Write(SecretMasker.Mask(buffer.ToString()));
    }
}
