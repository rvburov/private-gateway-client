using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Time.Testing;
using PrivateGateway.Client.Core.Settings;
using PrivateGateway.Client.Core.Updates;
using PrivateGateway.Client.Service;
using PrivateGateway.Client.Service.Updates;

namespace PrivateGateway.Client.Tests.Network;

/// <summary>
/// Сервер уже отдал заголовки, но остановил тело: общий срок загрузки по-прежнему действует. Ленту служба не качает —
/// ее передает окно (срок ее запроса — у API сайта окна, AccountApiTests).
/// </summary>
public sealed class UpdateTransferTimeoutTests
{
    [Fact]
    public async Task Package_deadline_reports_failure_and_removes_the_partial_file()
    {
        var time = new FakeTimeProvider();
        using var body = new StalledBody();
        using var http = new HttpClient(new ResponseHandler(body));
        var folder = Directory.CreateTempSubdirectory("pg-update-deadline-").FullName;
        try
        {
            var asset = new UpdateAsset("package.msi", Convert.ToHexStringLower(SHA256.HashData([1])), 1);
            var manifest = new UpdateManifest("0.2.0", UpdateChannel.Stable, 100, time.GetUtcNow(), new Uri("https://example.com/notes"), [],
                new Dictionary<string, UpdateAsset> { ["win-x64-msi"] = asset });
            var installer = new UpdateInstaller(http, folder,
                new UpdateKind("win-x64-msi", (_, _) => throw new InvalidOperationException("установка не должна запускаться")),
                new ServiceJournal(time), time);

            var pending = CollectAsync(installer.DownloadAsync(manifest, new Uri("https://example.com/api/app/updates/files/t1"), TestContext.Current.CancellationToken));
            await body.Reading.Task.WaitAsync(TestContext.Current.CancellationToken);
            time.Advance(UpdateInstaller.TransferTimeout);
            var progress = await pending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            progress.ShouldNotBeEmpty();
            progress[^1].Error.ShouldNotBeNull();
            progress.ShouldNotContain(p => p.Ready);
            Directory.GetFiles(folder).ShouldBeEmpty("незавершенный пакет после тайм-аута удален");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private static async Task<List<UpdateProgress>> CollectAsync(IAsyncEnumerable<UpdateProgress> source)
    {
        var progress = new List<UpdateProgress>();
        await foreach (var item in source)
        {
            progress.Add(item);
        }

        return progress;
    }

    private sealed class ResponseHandler(Stream body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) });
    }

    private sealed class StalledBody : Stream
    {
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Reading.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
