using PrivateGateway.Client.Infrastructure.Secrets;

namespace PrivateGateway.Client.Tests.Unit;

public sealed class KeychainAccessGateTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cleanup_restores_the_previous_interaction_setting_after_native_failure(bool original)
    {
        var allowed = original;
        List<bool> changes = [];
        var access = new KeychainAccessGate(() => allowed, next => { changes.Add(next); allowed = next; });

        Should.Throw<InvalidOperationException>(() => access.WithoutInteraction<int>(() =>
        {
            allowed.ShouldBeFalse("уборка не имеет права открыть системное окно");
            throw new InvalidOperationException("native failure");
        }));

        allowed.ShouldBe(original);
        changes.ShouldBe([false, original]);
        access.Run(() => allowed).ShouldBe(original);
    }

    [Fact]
    public void Cleanup_that_cannot_disable_interaction_does_not_run_the_native_delete()
    {
        var allowed = true;
        var ran = false;
        var access = new KeychainAccessGate(() => allowed, next =>
        {
            allowed = next;
            if (!next)
            {
                throw new InvalidOperationException("could not disable dialogs");
            }
        });

        Should.Throw<InvalidOperationException>(() => access.WithoutInteraction(() => ran = true));

        ran.ShouldBeFalse();
        allowed.ShouldBeTrue("даже неудачная смена режима восстанавливает исходное значение");
    }

    [Fact]
    public void A_failed_native_restore_is_retried_before_reading_any_other_secret()
    {
        var allowed = true;
        var failRestore = true;
        var access = new KeychainAccessGate(() => allowed, next =>
        {
            if (next && failRestore)
            {
                failRestore = false;
                throw new InvalidOperationException("native restore failed");
            }
            allowed = next;
        });
        Should.Throw<InvalidOperationException>(() => access.WithoutInteraction(() => 1));
        allowed.ShouldBeFalse();

        access.Run(() => allowed.ShouldBeTrue("следующий секрет не читается при невосстановленном флаге"));
    }

    [Fact]
    public async Task Reading_a_key_waits_for_cleanup_to_restore_interaction()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var allowed = true;
        using var cleanupEntered = new ManualResetEventSlim();
        using var releaseCleanup = new ManualResetEventSlim();
        using var readStarted = new ManualResetEventSlim();
        var access = new KeychainAccessGate(() => allowed, next => allowed = next);
        var cleanup = Task.Factory.StartNew(() => access.WithoutInteraction(() =>
        {
            cleanupEntered.Set();
            releaseCleanup.Wait(TimeSpan.FromSeconds(10), cancellationToken).ShouldBeTrue();
            return 1;
        }), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task<bool>? read = null;
        try
        {
            cleanupEntered.Wait(TimeSpan.FromSeconds(10), cancellationToken).ShouldBeTrue();
            read = Task.Factory.StartNew(() =>
            {
                readStarted.Set();
                return access.Run(() => allowed);
            }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            readStarted.Wait(TimeSpan.FromSeconds(10), cancellationToken).ShouldBeTrue();
            await Should.ThrowAsync<TimeoutException>(() => read.WaitAsync(TimeSpan.FromMilliseconds(200), cancellationToken));
        }
        finally
        {
            releaseCleanup.Set();
            await cleanup.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        (await read!.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken)).ShouldBeTrue("чтение общего ключа сохраняет возможность запросить доступ");
    }
}
