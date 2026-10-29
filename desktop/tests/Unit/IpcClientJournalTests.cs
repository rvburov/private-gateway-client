using PrivateGateway.Client.Application.Connection;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Infrastructure.Service;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>
/// Служба не отвечает окну — причина в журнале окна (экран «Проверка» и отчет), одной строкой, а не пустой журнал:
/// у пользователя на Windows отчет говорил «служба не ответила» и ничего больше (2026-10-29). Повтор той же причины
/// пишется одной строкой — его тест не ловит: следующую попытку gRPC делает не раньше своей паузы переподключения.
/// </summary>
public sealed class IpcClientJournalTests
{
    [Fact]
    public async Task Unreachable_service_leaves_the_reason_in_the_window_journal()
    {
        var endpoint = Path.Combine(Path.GetTempPath(), "pg-absent-" + Guid.NewGuid().ToString("N")[..8] + ".sock");
        using var client = new ServiceGatewayClient(IpcChannel.Create(endpoint, system: false), ServiceKind.User, _ => null,
            () => AppSettings.Default, TimeProvider.System);

        for (var i = 0; i < 100 && client.Journal.Count == 0; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        client.Journal.ShouldNotBeEmpty();
        var line = client.Journal[0];
        line.Level.ShouldBe(JournalLevel.Warning);
        line.Message.ShouldStartWith("Нет связи со службой: Unavailable");
    }
}
