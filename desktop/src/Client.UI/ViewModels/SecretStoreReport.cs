using PrivateGateway.Client.Application.Secrets;
using PrivateGateway.Client.UI.Localization;

namespace PrivateGateway.Client.UI.ViewModels;

/// <summary>Диагностика переноса Связки ключей: отчет никогда не читает ключи и не вызывает системные окна.</summary>
public static class SecretStoreReport
{
    public static IReadOnlyList<string> Lines(ISecretStore? store)
    {
        var loc = Loc.Instance;
        SecretStoreDiagnostics? state;
        try
        {
            state = store?.Diagnostics;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or System.Data.Common.DbException or System.Security.SecurityException)
        {
            return [loc["diag.report.vault.title"], "  " + loc["diag.report.vault.unavailable"], string.Empty];
        }

        return state is null ? [] :
        [
            loc["diag.report.vault.title"],
            "  " + loc.Format("diag.report.vault.counts", state.EncryptedEntries, state.PendingLegacyCleanup, state.DeletedEntries),
            "  " + loc[state.KeyUnlocked ? "diag.report.vault.open" : "diag.report.vault.closed"],
            "  " + loc["diag.report.vault.scope"],
            string.Empty,
        ];
    }
}
