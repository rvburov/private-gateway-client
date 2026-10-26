namespace PrivateGateway.Client.Core.Updates;

/// <summary>
/// Что служба ответила окну об обновлении: состояние, а если есть новее — версия, главное из заметок и страница
/// выпуска; когда проверяла.
/// </summary>
public sealed record UpdateOffer(UpdateState State, DateTimeOffset Checked, string? Version = null, IReadOnlyList<string>? Highlights = null, Uri? Notes = null)
{
    /// <summary>Найденный выпуск из подписанного манифеста.</summary>
    public static UpdateOffer Found(UpdateManifest manifest, DateTimeOffset checkedAt)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new UpdateOffer(UpdateState.Available, checkedAt, manifest.Version, manifest.Highlights, manifest.Notes);
    }
}
