using PrivateGateway.Client.Core.Inspector;

namespace PrivateGateway.Client.Core.Lab;

/// <summary>Шаг разбора «почему сайт не открывается»: что проверяется — по порядку, как идет запрос браузера.</summary>
public enum DiagnosisStepKind
{
    /// <summary>Сеть машины: адаптер выхода и шлюз.</summary>
    Network,

    /// <summary>Защита не держит сеть закрытой: блок, ошибка, подключение.</summary>
    Protection,

    /// <summary>Имя сайта находится через DNS.</summary>
    Dns,

    /// <summary>Сайт принимает соединение TCP на 443.</summary>
    Tcp,

    /// <summary>Рукопожатие TLS проходит, сертификат в порядке.</summary>
    Tls,
}

/// <summary>Итог шага.</summary>
public enum DiagnosisResult
{
    Ok,

    Failed,

    /// <summary>Не проверялось: раньше нашлась причина или замера нет.</summary>
    Skipped,
}

/// <summary>Вывод разбора — первая найденная причина или «сайт отвечает».</summary>
public enum DiagnosisVerdict
{
    /// <summary>Служба не ответила — замеров нет.</summary>
    NoService,

    /// <summary>Нет включенного адаптера с маршрутом в интернет или шлюзом.</summary>
    NoNetwork,

    /// <summary>Защита держит сеть закрытой: блок при обрыве, строгий режим, ошибка, подключение.</summary>
    ProtectionBlocks,

    /// <summary>DNS ответил, что такого имени нет.</summary>
    NameNotFound,

    /// <summary>DNS не ответил.</summary>
    DnsFailed,

    /// <summary>Узел отказал в соединении на 443.</summary>
    PortRefused,

    /// <summary>Узел не ответил на соединение — молчит или ответ теряется по дороге.</summary>
    NoAnswer,

    /// <summary>До узла нет маршрута.</summary>
    Unreachable,

    /// <summary>Сертификат сайта не в порядке.</summary>
    Certificate,

    /// <summary>Рукопожатие TLS сорвалось.</summary>
    TlsFailed,

    /// <summary>Все шаги прошли: сеть и сайт отвечают — дело в браузере или самом сайте.</summary>
    SiteAnswers,
}

/// <summary>Шаг разбора.</summary>
/// <param name="Kind">Что проверялось.</param>
/// <param name="Result">Итог.</param>
/// <param name="Detail">Что нашлось: «адаптер → шлюз», адрес сайта, версия TLS, код ошибки или состояние защиты; нет — null.</param>
/// <param name="Outcome">Итог замера службы, если шаг — замер; нет — null.</param>
/// <param name="Time">Время ответа; нет — null.</param>
/// <param name="Problems">Что не так с сертификатом; нет — null.</param>
public sealed record DiagnosisStep(
    DiagnosisStepKind Kind,
    DiagnosisResult Result,
    string? Detail = null,
    ProbeOutcome? Outcome = null,
    TimeSpan? Time = null,
    IReadOnlyList<TlsProblem>? Problems = null);

/// <summary>Разбор: шаги по порядку и вывод.</summary>
public sealed record SiteDiagnosis(IReadOnlyList<DiagnosisStep> Steps, DiagnosisVerdict Verdict);

/// <summary>Замеры для разбора: сеть машины, состояние защиты и замеры службы — DNS, TCP 443 и TLS 443; не мерили — null.</summary>
public sealed record SiteFacts(NetworkSnapshot? Snapshot, ConnectionStatus Status, IReadOnlyList<ProbeStep>? Dns = null, ProbeStep? Tcp = null, ProbeStep? Tls = null);

/// <summary>
/// «Почему сайт не открывается» (лаборатория, этап 8): шаги в том порядке, в каком идет запрос браузера — сеть, защита, DNS,
/// TCP, TLS. Первый неудачный шаг — вывод, следующие — «не проверялось»: причину называет замер, а не догадка.
/// </summary>
public static class SiteDoctor
{
    public static SiteDiagnosis Diagnose(SiteFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Snapshot is null && facts.Dns is null)
        {
            return new SiteDiagnosis([.. Enum.GetValues<DiagnosisStepKind>().Select(k => new DiagnosisStep(k, DiagnosisResult.Skipped))], DiagnosisVerdict.NoService);
        }

        var steps = new List<DiagnosisStep>();
        DiagnosisVerdict? verdict = null;

        void Add(DiagnosisStep step, DiagnosisVerdict? failure)
        {
            if (verdict is not null)
            {
                steps.Add(new DiagnosisStep(step.Kind, DiagnosisResult.Skipped));
                return;
            }

            steps.Add(step);
            if (step.Result == DiagnosisResult.Failed)
            {
                verdict = failure;
            }
        }

        // Сеть машины: адаптер выхода — с маршрутом по умолчанию или шлюзом (у сотового модема PPP маршрут есть, а шлюза нет).
        // Снимка нет — шаг не проверялся, дальше решают замеры.
        var card = PathBuilder.Physical(facts.Snapshot);
        if (facts.Snapshot is null)
        {
            Add(new DiagnosisStep(DiagnosisStepKind.Network, DiagnosisResult.Skipped), null);
        }
        else if (card is not null)
        {
            Add(new DiagnosisStep(DiagnosisStepKind.Network, DiagnosisResult.Ok, card.Gateways.Count > 0 ? $"{card.Name} → {card.Gateways[0]}" : card.Name), null);
        }
        else
        {
            Add(new DiagnosisStep(DiagnosisStepKind.Network, DiagnosisResult.Failed), DiagnosisVerdict.NoNetwork);
        }

        // Защита: блок при обрыве, строгий режим, ошибка под блоком или подключение держат сеть закрытой.
        var status = facts.Status;
        var closed = status is { StrictBlock: true } or { State: ConnectionState.Blocked or ConnectionState.Connecting or ConnectionState.Reconnecting }
            || status is { State: ConnectionState.Error } && status.Protection.AllTraffic != CheckState.Off;
        Add(new DiagnosisStep(DiagnosisStepKind.Protection, closed ? DiagnosisResult.Failed : DiagnosisResult.Ok,
            closed ? status.ErrorCode ?? status.State.ToString() : null), DiagnosisVerdict.ProtectionBlocks);

        // DNS: адрес нашелся — дальше; «нет такого имени» и «не ответил» — разные причины.
        if (facts.Dns is null)
        {
            Add(new DiagnosisStep(DiagnosisStepKind.Dns, DiagnosisResult.Skipped), null);
        }
        else if (facts.Dns.FirstOrDefault(s => s is { Outcome: ProbeOutcome.Ok, Address: not null }) is { } found)
        {
            Add(new DiagnosisStep(DiagnosisStepKind.Dns, DiagnosisResult.Ok, found.Address!.ToString(), ProbeOutcome.Ok, found.Time), null);
        }
        else
        {
            var notFound = facts.Dns.Any(s => s.Outcome == ProbeOutcome.NotFound);
            Add(new DiagnosisStep(DiagnosisStepKind.Dns, DiagnosisResult.Failed, null, notFound ? ProbeOutcome.NotFound : facts.Dns.Count > 0 ? facts.Dns[^1].Outcome : ProbeOutcome.Failed),
                notFound ? DiagnosisVerdict.NameNotFound : DiagnosisVerdict.DnsFailed);
        }

        // TCP 443: отказ, молчание и «нет маршрута» — разные причины.
        if (facts.Tcp is not { } tcp)
        {
            Add(new DiagnosisStep(DiagnosisStepKind.Tcp, DiagnosisResult.Skipped), null);
        }
        else
        {
            Add(new DiagnosisStep(DiagnosisStepKind.Tcp, tcp.Outcome == ProbeOutcome.Ok ? DiagnosisResult.Ok : DiagnosisResult.Failed,
                    tcp.Address?.ToString(), tcp.Outcome, tcp.Time),
                tcp.Outcome switch
                {
                    ProbeOutcome.Refused => DiagnosisVerdict.PortRefused,
                    ProbeOutcome.Unreachable => DiagnosisVerdict.Unreachable,
                    _ => DiagnosisVerdict.NoAnswer,
                });
        }

        // TLS 443: рукопожатие прошло, но сертификат с изъяном — тоже причина: браузер такой сайт не откроет.
        if (facts.Tls is not { } handshake)
        {
            Add(new DiagnosisStep(DiagnosisStepKind.Tls, DiagnosisResult.Skipped), null);
        }
        else if (handshake.Tls is { } tls)
        {
            Add(new DiagnosisStep(DiagnosisStepKind.Tls, tls.Problems.Count == 0 ? DiagnosisResult.Ok : DiagnosisResult.Failed,
                tls.Protocol, handshake.Outcome, handshake.Time, tls.Problems), DiagnosisVerdict.Certificate);
        }
        else
        {
            Add(new DiagnosisStep(DiagnosisStepKind.Tls, DiagnosisResult.Failed, null, handshake.Outcome), DiagnosisVerdict.TlsFailed);
        }

        // Причины нет и TLS прошел — сайт отвечает; замеры не дошли до TLS без причины — служба перестала отвечать.
        return new SiteDiagnosis(steps, verdict ?? (steps[^1].Result == DiagnosisResult.Ok ? DiagnosisVerdict.SiteAnswers : DiagnosisVerdict.NoService));
    }

    /// <summary>Нужен ли следующий замер: все прежние шаги прошли или не проверялись, причина еще не найдена.</summary>
    public static bool Continue(SiteFacts facts) =>
        Diagnose(facts).Steps.All(s => s.Result != DiagnosisResult.Failed);
}
