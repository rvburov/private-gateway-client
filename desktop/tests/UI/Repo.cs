namespace PrivateGateway.Client.Tests.UI;

/// <summary>Пути репозитория для тестов, которые читают исходники и пишут снимки.</summary>
internal static class Repo
{
    public static string Root { get; } = Find();

    public static string UiSource => Path.Combine(Root, "desktop", "src", "Client.UI");

    public static string Snapshots
    {
        get
        {
            var path = Path.Combine(Root, "artifacts", "ui-snapshots");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    private static string Find()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PrivateGateway.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Не найден корень репозитория (PrivateGateway.slnx)");
    }
}
