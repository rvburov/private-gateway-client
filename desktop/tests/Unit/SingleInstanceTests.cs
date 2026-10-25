using PrivateGateway.Client.Infrastructure.Storage;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Одна копия программы на папку данных.</summary>
public sealed class SingleInstanceTests : IDisposable
{
    private readonly AppPaths _paths = new(Path.Combine(Path.GetTempPath(), "pg-instance-" + Guid.NewGuid().ToString("N")));

    public void Dispose()
    {
        if (Directory.Exists(_paths.DataDirectory))
        {
            Directory.Delete(_paths.DataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Second_copy_does_not_start_and_asks_the_first_to_show_its_window()
    {
        using var first = SingleInstance.TryAcquire(_paths).ShouldNotBeNull();
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ActivationRequested += (_, _) => activated.TrySetResult();

        SingleInstance.TryAcquire(_paths).ShouldBeNull("первая копия уже работает");
        SingleInstance.RequestActivation(_paths);

        await activated.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void After_the_first_copy_exits_the_next_one_starts()
    {
        SingleInstance.TryAcquire(_paths).ShouldNotBeNull().Dispose();

        using var next = SingleInstance.TryAcquire(_paths);

        next.ShouldNotBeNull();
    }

    [Fact]
    public void Another_data_folder_is_another_copy()
    {
        var other = new AppPaths(_paths.DataDirectory + "-other");
        try
        {
            using var first = SingleInstance.TryAcquire(_paths).ShouldNotBeNull();
            using var second = SingleInstance.TryAcquire(other);
            second.ShouldNotBeNull();
        }
        finally
        {
            Directory.Delete(other.DataDirectory, recursive: true);
        }
    }
}
