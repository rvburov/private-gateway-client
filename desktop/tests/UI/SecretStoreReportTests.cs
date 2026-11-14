using Avalonia.Headless.XUnit;
using PrivateGateway.Client.Application.Secrets;
using PrivateGateway.Client.UI.Localization;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.Tests.UI;

public sealed class SecretStoreReportTests
{
    [AvaloniaFact]
    public void Report_reads_only_counts_and_never_opens_a_secret()
    {
        Loc.Instance.SetLanguage("ru");
        var store = new Store();
        var text = string.Join('\n', SecretStoreReport.Lines(store));
        text.ShouldContain("Зашифрованных записей: 30");
        text.ShouldContain("ожидают очистки старой записи: 20");
        text.ShouldContain("еще не открыт");
        text.ShouldContain("не определялось");
        text.ShouldNotContain(store.Name);
        store.Reads.ShouldBe(0);
    }

    [AvaloniaFact]
    public void Broken_metadata_does_not_prevent_export_or_expose_error_details()
    {
        Loc.Instance.SetLanguage("ru");
        var store = new Store { Broken = true };
        var text = string.Join('\n', SecretStoreReport.Lines(store));
        text.ShouldContain("недоступно");
        text.ShouldNotContain("secret-marker");
        store.Reads.ShouldBe(0);
    }

    private sealed class Store : ISecretStore
    {
        public string Name => "secret-marker";
        public bool IsAvailable => true;
        public bool Broken { get; init; }
        public int Reads { get; private set; }
        public SecretStoreDiagnostics? Diagnostics => Broken ? throw new IOException("secret-marker") : new("keychain-file-v1", 30, 20, 2, false);
        public void Save(string key, string secret) => throw new InvalidOperationException();
        public void Delete(string key) => throw new InvalidOperationException();
        public string? Read(string key)
        {
            Reads++;
            throw new InvalidOperationException("Отчет не должен открывать секрет");
        }
    }
}
