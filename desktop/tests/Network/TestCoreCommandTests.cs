using System.Diagnostics;

namespace PrivateGateway.Client.Tests.Network;

public sealed class TestCoreCommandTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Deadline_covers_a_blocked_command_and_its_input_and_preserves_other_processes(bool largeInput, bool cancel)
    {
        using var outside = Process.Start(Sleep()).ShouldNotBeNull();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var timeout = TimeSpan.FromSeconds(1);
        if (cancel)
        {
            cancellation.CancelAfter(timeout);
        }

        var command = 0;
        var run = Task.Run(() => TestCoreCommand.Run(Sleep(), largeInput ? new string('x', 8 * 1024 * 1024) : null,
            cancel ? TimeSpan.FromMinutes(1) : timeout, process => Volatile.Write(ref command, process.Id), cancellation.Token));
        try
        {
            // Внешняя граница нужна и мутанту без отмены записи stdin: тест снимет собственный процесс в finally.
            var completed = run.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            if (cancel)
            {
                await Should.ThrowAsync<OperationCanceledException>(() => completed);
            }
            else
            {
                var error = await Should.ThrowAsync<TimeoutException>(() => completed);
                error.Message.ShouldContain("не ответил за", Case.Sensitive, "сработал срок команды, а не внешняя граница самого теста");
            }

            command.ShouldBeGreaterThan(0);
            Alive(command).ShouldBeFalse("снята именно команда, превысившая срок");
            outside.HasExited.ShouldBeFalse("соседний процесс того же имени не затронут");
        }
        finally
        {
            Stop(command);
            Stop(outside.Id);
            try
            {
                await run.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException or IOException)
            {
                // Ожидаемая ошибка команды уже проверена выше; после снятия мутант тоже должен закончить свою задачу.
            }
        }
    }

    private static ProcessStartInfo Sleep() => OperatingSystem.IsWindows()
        ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"))
        { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "-n", "30", "127.0.0.1" } }
        : new ProcessStartInfo("sleep") { UseShellExecute = false, ArgumentList = { "30" } };

    private static bool Alive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void Stop(int pid)
    {
        if (pid <= 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(5)).ShouldBeTrue("процесс стенда освобожден");
            }
        }
        catch (ArgumentException)
        {
            // Процесс уже вышел.
        }
    }
}
