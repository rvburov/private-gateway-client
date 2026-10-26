namespace PrivateGateway.Client.Core.Updates;

/// <summary>
/// Что служба ответила окну об обновлении: состояние, а если есть новее — версия, главное из заметок и страница
/// выпуска; когда проверяла.
/// </summary>
public sealed record UpdateOffer(UpdateState State, DateTimeOffset Checked, string? Version = null, IReadOnlyList<string>? Highlights = null, Uri? Notes = null)
{
    /// <summary>Служба поставит его сама (MSI, .deb, .rpm установленной программы); нет — только страница выпуска.</summary>
    public bool Installable { get; init; }

    /// <summary>Найденный выпуск из подписанного манифеста.</summary>
    public static UpdateOffer Found(UpdateManifest manifest, DateTimeOffset checkedAt)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new UpdateOffer(UpdateState.Available, checkedAt, manifest.Version, manifest.Highlights, manifest.Notes);
    }
}

/// <summary>
/// Ход загрузки обновления службой: сколько получено из скольких; последнее — готово (файл сверен с манифестом) или
/// отказ с причиной.
/// </summary>
public sealed record UpdateProgress(long Received, long Total, bool Ready = false, string? Error = null)
{
    public bool Failed => Error is not null;
}
