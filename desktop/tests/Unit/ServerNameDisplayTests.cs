using System.Text.Json;
using PrivateGateway.Client.Application.Servers;

namespace PrivateGateway.Client.Tests.Unit;

public sealed class ServerNameDisplayTests
{
    [Fact]
    public void Display_spaces_preserve_graphemes_and_existing_spacing_on_shared_mobile_fixtures()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "PrivateGateway.slnx")))
        {
            root = root.Parent;
        }

        root.ShouldNotBeNull();
        using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "shared", "testdata", "server-name-display.json")));
        foreach (var fixture in fixtures.RootElement.EnumerateArray())
        {
            var input = fixture.GetProperty("input").GetString()!;
            var expected = fixture.GetProperty("expected").GetString()!;
            ServerNameDisplay.Format(input).ShouldBe(expected, input);
            ServerNameDisplay.Format(expected).ShouldBe(expected, "повторное отображение не добавляет пробелы");
        }
    }

    [Fact]
    public void Malformed_utf16_is_not_a_reason_to_crash_the_server_list() =>
        ServerNameDisplay.Format("\uD800⚡Name").ShouldBe("\uD800⚡ Name");
}
