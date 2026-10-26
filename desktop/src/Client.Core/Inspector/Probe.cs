using System.Globalization;
using System.Net;

namespace PrivateGateway.Client.Core.Inspector;

/// <summary>Замер инспектора: до какого узла и чем.</summary>
public enum ProbeKind
{
    /// <summary>Эхо ICMP — отвечает ли узел и за сколько.</summary>
    Ping,

    /// <summary>Трассировка: эхо ICMP с растущим TTL — через какие узлы идет путь.</summary>
    Trace,

    /// <summary>Поиск имени через DNS системы — какие адреса у имени.</summary>
    Dns,

    /// <summary>Соединение TCP с портом — открыт ли он.</summary>
    Tcp,

    /// <summary>Рукопожатие TLS — версия, шифр и сертификат.</summary>
    Tls,
}

/// <summary>Чем кончилась попытка замера.</summary>
public enum ProbeOutcome
{
    /// <summary>Ответил: эхо, адрес имени, открытый порт, рукопожатие.</summary>
    Ok,

    /// <summary>Промежуточный узел трассировки: срок жизни пакета кончился на нем.</summary>
    Hop,

    /// <summary>Ответа не было за отведенное время.</summary>
    TimedOut,

    /// <summary>Порт закрыт: узел отказал в соединении.</summary>
    Refused,

    /// <summary>Узел или сеть недоступны: так сказал роутер или ОС.</summary>
    Unreachable,

    /// <summary>Имя не найдено или у него нет адресов.</summary>
    NotFound,

    /// <summary>Другой отказ: замер не удался (рукопожатие TLS оборвано, ОС не дала отправить эхо).</summary>
    Failed,
}

/// <summary>Что не так с сертификатом узла.</summary>
public enum TlsProblem
{
    /// <summary>Сертификат выдан на другое имя.</summary>
    NameMismatch,

    /// <summary>Срок сертификата кончился.</summary>
    Expired,

    /// <summary>Срок сертификата еще не начался.</summary>
    NotYetValid,

    /// <summary>Цепочка не ведет к доверенному корню (самоподписанный, неполная цепочка, чужой корень).</summary>
    Untrusted,

    /// <summary>Другая ошибка цепочки.</summary>
    Other,
}

/// <summary>Что показало рукопожатие TLS.</summary>
/// <param name="Protocol">Версия: «TLS 1.3».</param>
/// <param name="Cipher">Набор шифров, как его называет ОС.</param>
/// <param name="Alpn">Согласованный протокол приложения (h2, http/1.1); не согласован — null.</param>
/// <param name="Subject">Кому выдан сертификат.</param>
/// <param name="Issuer">Кем выдан.</param>
/// <param name="NotBefore">Действует с.</param>
/// <param name="NotAfter">Действует до.</param>
/// <param name="Names">Имена сертификата (SAN).</param>
/// <param name="Problems">Что не так; все в порядке — пусто.</param>
public sealed record TlsFacts(
    string Protocol,
    string Cipher,
    string? Alpn,
    string Subject,
    string Issuer,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    IReadOnlyList<string> Names,
    IReadOnlyList<TlsProblem> Problems);

/// <summary>Строка хода замера.</summary>
/// <param name="Index">Номер эха, участок трассировки (TTL) или номер адреса имени; у TCP и TLS — 1.</param>
/// <param name="Address">Кто ответил или к кому шли; неизвестно — null.</param>
/// <param name="Time">Время ответа; ответа нет — null.</param>
/// <param name="Outcome">Чем кончилось.</param>
/// <param name="Tls">Рукопожатие TLS — только у замера TLS.</param>
public sealed record ProbeStep(int Index, IPAddress? Address, TimeSpan? Time, ProbeOutcome Outcome, TlsFacts? Tls = null);

/// <summary>
/// Узел замера: IP или имя хоста. Имя — только буквы, цифры, дефис и точки (международное — в виде punycode), не длиннее
/// 253 знаков; другое — не узел: служба команд и адресов с путями не принимает.
/// </summary>
public static class ProbeTarget
{
    /// <summary>Наибольший номер порта.</summary>
    public const int MaxPort = 65535;

    /// <summary>Узел в виде, который служба примет; не узел — null. Пробелы по краям и точка в конце имени отбрасываются.</summary>
    public static string? Normalize(string? text)
    {
        var value = text?.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        // IPv6 — и в скобках, как в адресной строке; IPv4 — только четыре числа (TryParse принял бы и «1.2.3»).
        if (IPAddress.TryParse(value, out var address) && (value.Contains(':', StringComparison.Ordinal) || value.Count(c => c == '.') == 3))
        {
            return address.ToString();
        }

        // Имя из ASCII проверяется только здесь — одинаково на всех ОС; международное сначала переводится в punycode
        // (IdnMapping на Windows — средствами ОС, на Linux и macOS — ICU).
        string ascii;
        try
        {
            ascii = value.All(char.IsAscii) ? value.ToLowerInvariant() : new IdnMapping().GetAscii(value).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return null;
        }

        return ascii.Length <= 253 && ascii.Split('.').All(Label) && !ascii.Split('.')[^1].All(char.IsAsciiDigit) ? ascii : null;

        static bool Label(string label) =>
            label.Length is > 0 and <= 63 && label[0] != '-' && label[^1] != '-' && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }
}
