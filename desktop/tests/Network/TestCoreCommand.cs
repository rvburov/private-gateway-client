using System.Diagnostics;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>Короткая команда ядра: общий срок охватывает stdin, оба потока вывода и выход процесса.</summary>
internal static class TestCoreCommand
{
    public static (int Code, string Output) Run(ProcessStartInfo start, string? input, TimeSpan timeout,
        Action<Process>? started = null, CancellationToken cancellationToken = default) =>
        RunAsync(start, input, timeout, started, cancellationToken).GetAwaiter().GetResult();

    private static async Task<(int Code, string Output)> RunAsync(ProcessStartInfo start, string? input, TimeSpan timeout,
        Action<Process>? started, CancellationToken cancellationToken)
    {
        start.UseShellExecute = false;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Команда ядра не запустилась");
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(timeout);
        Task? operation = null;
        try
        {
            started?.Invoke(process);
            var output = process.StandardOutput.ReadToEndAsync(wait.Token);
            var errors = process.StandardError.ReadToEndAsync(wait.Token);
            operation = Task.WhenAll(output, errors, InputAsync(process, input, wait.Token), process.WaitForExitAsync(wait.Token));
            // Общая граница нужна и на случай, если отдельная операция канала не завершится по своему token.
            await operation.WaitAsync(wait.Token).ConfigureAwait(false);
            var text = await output.ConfigureAwait(false);
            return (process.ExitCode, text + await errors.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{Path.GetFileName(start.FileName)} не ответил за {timeout.TotalSeconds:0.#} с");
        }
        finally
        {
            // Только процесс этой команды и его потомки. Не снимать все процессы по имени ядра.
            Stop(process);
            if (operation is not null)
            {
                try
                {
                    await operation.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException or TimeoutException)
                {
                    // Первичный отказ передан вызывающему коду; после снятия дочитываем или наблюдаем ошибки каналов.
                }
            }
        }
    }

    private static void Stop(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception && process.HasExited)
        {
            // Вышел сам между проверкой и снятием.
        }

        if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
        {
            throw new TimeoutException("Процесс команды не завершился после снятия");
        }
    }

    private static async Task InputAsync(Process process, string? input, CancellationToken cancellationToken)
    {
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        // После Flush буфер пуст: Close не может зависнуть, дописывая конфиг процессу, который перестал читать stdin.
        process.StandardInput.Close();
    }
}
