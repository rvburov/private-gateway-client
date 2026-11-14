using System.ComponentModel;
using System.Reflection;
using PrivateGateway.Client.Service.Cores;

namespace PrivateGateway.Client.Tests.Network;

public sealed class CoreProcessStopTests
{
    // Управляем именно промежутком HasExited -> Kill, который реальный процесс закрывает недетерминированно.
    private static readonly Action<Func<bool>, Action> KillForStop = typeof(CoreProcess)
        .GetMethod("KillForStop", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<Action<Func<bool>, Action>>();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Exit_during_kill_is_success_only_after_the_process_is_confirmed_gone(bool nativeError)
    {
        var exited = false;
        var attempts = 0;
        var error = nativeError ? (Exception)new Win32Exception(5) : new InvalidOperationException("already exited");

        KillForStop(() => exited, () =>
        {
            attempts++;
            exited = true;
            throw error;
        });

        attempts.ShouldBe(1);
        exited.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_failed_kill_of_a_live_process_is_not_reported_as_success(bool nativeError)
    {
        var error = nativeError ? (Exception)new Win32Exception(5) : new InvalidOperationException("not stopped");

        var actual = Should.Throw<Exception>(() => KillForStop(() => false, () => throw error));

        actual.ShouldBeSameAs(error, "отказ остановки остается виден и владение процессом не теряется");
    }

    [Fact]
    public void An_already_exited_process_does_not_need_another_kill() =>
        KillForStop(() => true, () => throw new InvalidOperationException("Kill must not be called"));
}
