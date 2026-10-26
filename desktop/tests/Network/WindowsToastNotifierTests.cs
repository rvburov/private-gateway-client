using System.Runtime.Versioning;
using Microsoft.Win32;
using PrivateGateway.Client.Application.Notifications;
using PrivateGateway.Client.Infrastructure.Notifications;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Уведомления Windows через WinRT по-настоящему: разметка, метка, обработчик нажатия, имя программы в реестре, показ и
/// уборка. Программа теста — со своим AppUserModelID, после теста его ключ удаляется.
/// </summary>
public sealed class WindowsToastNotifierTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [SupportedOSPlatform("windows10.0.10240")]
    public async Task Toast_is_built_through_winrt_shown_and_taken_away()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
        {
            Assert.Skip("уведомления Windows — на Windows 10 и новее");
        }

        var notifier = new WindowsToastNotifier("PrivateGateway.Tests." + Guid.NewGuid().ToString("N")[..8], "Private Gateway (тест)");
        var text = new NoticeText("Соединение потеряно", "Интернет заблокирован, пока VPN не вернется. Переподключаюсь к «A & <B>»…",
            [new(NoticeAction.ChangeServer, "Сменить сервер"), new(NoticeAction.Open, "Открыть")]);
        try
        {
            notifier.TryBuild(Notice.ProtectionTag, text, important: true).ShouldBeNull("разметка, метка и обработчик нажатия — через WinRT");

            await notifier.ShowAsync(Notice.ProtectionTag, text, important: false, Ct);
            using (var key = Registry.CurrentUser.OpenSubKey(notifier.RegistryKey))
            {
                key.ShouldNotBeNull("имя программы для уведомлений — в реестре пользователя");
                key.GetValue("DisplayName").ShouldBe("Private Gateway (тест)");
            }

            await notifier.CloseAsync(Notice.ProtectionTag, Ct);
            if (notifier.LastError is { } error)
            {
                // Машина без сеанса пользователя (служба сборки) уведомлений не показывает — причина видна в отчете.
                Assert.Skip($"Windows не показала уведомление: 0x{error.HResult:X8} {error.Message}");
            }
        }
        finally
        {
            notifier.Unregister();
        }
    }
}
