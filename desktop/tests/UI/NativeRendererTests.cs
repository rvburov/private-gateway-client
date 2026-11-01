using SkiaSharp;

namespace PrivateGateway.Client.Tests.UI;

/// <summary>
/// Обычный Fact делает отказ библиотеки Skia явной ошибкой прогона, даже если AvaloniaFact потеряны в cleanup.
/// </summary>
public sealed class NativeRendererTests
{
    [Fact]
    public void Native_renderer_draws_a_pixel_before_headless_tests_are_trusted()
    {
        using var surface = SKSurface.Create(new SKImageInfo(2, 2)).ShouldNotBeNull();
        var color = new SKColor(19, 97, 181, 255);
        surface.Canvas.Clear(color);

        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image).ShouldNotBeNull();
        bitmap.GetPixel(1, 1).ShouldBe(color);
    }
}
