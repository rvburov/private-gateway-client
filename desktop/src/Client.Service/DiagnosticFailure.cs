using System.Globalization;
using System.Net.Sockets;
using Grpc.Core;
using PrivateGateway.Client.Core;

namespace PrivateGateway.Client.Service;

/// <summary>Отказ одной диагностической операции не отменяет остальные. Секреты исключения не записываются.</summary>
internal static class DiagnosticFailure
{
    internal static async Task<T> RunAsync<T>(ServiceJournal journal, string operation, Func<Task<T>> action,
        Func<Exception, T> failed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = await action().WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception error) when (Recoverable(error))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record(journal, operation, error);
            return failed(error);
        }
    }

    internal static T Run<T>(ServiceJournal journal, string operation, Func<T> action, Func<Exception, T> failed,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var result = action();
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception error) when (Recoverable(error))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Record(journal, operation, error);
            return failed(error);
        }
    }

    internal static string Reason(Exception error) => error is TimeoutException or OperationCanceledException ? "timeout" : "probe_failed";

    private static bool Recoverable(Exception error) => error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private static void Record(ServiceJournal journal, string operation, Exception error)
    {
        // Только имена типов и числовые/перечислимые коды. Message, Data, StackTrace и RPC Detail могут содержать
        // адрес подписки, имя сайта, пароль или пути пользователя: даже после общей маскировки их сюда не передаем.
        var failures = new List<string>();
        for (Exception? current = error; current is not null && failures.Count < 3; current = current.InnerException)
        {
            var code = current switch
            {
                RpcException rpc => "; RPC=" + rpc.StatusCode,
                SocketException socket => "; Socket=" + socket.SocketErrorCode,
                HttpRequestException http => "; HTTP=" + http.HttpRequestError,
                _ => string.Empty,
            };
            failures.Add(current.GetType().FullName + "; HRESULT=0x" + current.HResult.ToString("X8", CultureInfo.InvariantCulture) + code);
        }

        journal.Add(JournalLevel.Warning, "service", "Проверка " + operation + " не выполнена: " + string.Join(" <- ", failures));
    }
}
