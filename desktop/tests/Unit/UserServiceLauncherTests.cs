using PrivateGateway.Client.Infrastructure.Service;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>Где окно берет файл службы пользователя: рядом с собой или в соседней папке сборки — и больше нигде.</summary>
public sealed class UserServiceLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pg-launcher-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Installed_service_lies_next_to_the_window()
    {
        var window = Folder("Program");
        var service = Touch(Path.Combine(window, UserServiceLauncher.FileName));
        Touch(Path.Combine(_root, "artifacts", "bin", "Client.Service", "Program", UserServiceLauncher.FileName));

        UserServiceLauncher.Find(window).ShouldBe(service);
    }

    [Fact]
    public void Built_service_comes_from_the_neighbour_build_folder_of_the_same_configuration()
    {
        var window = Folder("artifacts", "bin", "Client.UI", "release");
        var service = Touch(Path.Combine(_root, "artifacts", "bin", "Client.Service", "release", UserServiceLauncher.FileName));
        Touch(Path.Combine(_root, "artifacts", "bin", "Client.Service", "debug", UserServiceLauncher.FileName));

        UserServiceLauncher.Find(window + Path.DirectorySeparatorChar).ShouldBe(service);
    }

    [Theory]
    [InlineData("other", "bin", "Client.UI", "debug")]
    [InlineData("artifacts", "out", "Client.UI", "debug")]
    [InlineData("artifacts", "bin", "Client.Tests.UI", "debug")]
    public void Other_folders_are_not_searched(string a, string b, string c, string d)
    {
        var window = Folder(a, b, c, d);
        Touch(Path.Combine(_root, a, b, "Client.Service", d, UserServiceLauncher.FileName));

        UserServiceLauncher.Find(window).ShouldBeNull();
    }

    [Fact]
    public void Without_a_service_file_nothing_starts()
    {
        new UserServiceLauncher(null, Environment.ProcessId).Start().ShouldBeFalse();
        new UserServiceLauncher(Path.Combine(_root, "missing"), Environment.ProcessId).Start().ShouldBeFalse();
    }

    private string Folder(params string[] parts) => Directory.CreateDirectory(Path.Combine([_root, .. parts])).FullName;

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
