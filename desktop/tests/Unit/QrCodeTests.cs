using PrivateGateway.Client.Application.Import;

namespace PrivateGateway.Client.Tests.Unit;

/// <summary>QR-код: ссылка сервера → модули → картинка → тот же текст, как при «Поделиться» и импорте с экрана.</summary>
public sealed class QrCodeTests
{
    private static readonly string Link =
        File.ReadAllText(Path.Combine(FindRoot(), "shared", "testdata", "links", "vless", "private-gateway.txt")).Trim();

    [Fact]
    public void Link_survives_encoding_and_reading_back_from_a_screenshot()
    {
        var modules = QrCode.Encode(Link);

        modules.GetLength(0).ShouldBe(modules.GetLength(1));
        // Как снимок области экрана: код в углу большой картинки, по 4 пикселя на модуль.
        QrCode.Decode(Render(modules, scale: 4, canvas: 900, offset: 37)).ShouldBe(Link);
    }

    [Fact]
    public void Modules_are_rows_then_columns_not_a_mirror()
    {
        var modules = QrCode.Encode(Link);
        var size = modules.GetLength(0);

        // ISO/IEC 18004: «темный модуль» всегда в столбце 8 над нижним левым искателем; в зеркальном коде
        // он оказался бы в строке 8, и часть читателей такой код не примет.
        modules[size - 8, 8].ShouldBeTrue();
        modules[8, size - 8].ShouldBeFalse("для этой ссылки бит формата здесь светлый — зеркало заметно");
    }

    [Fact]
    public void Light_code_on_dark_background_is_read_too()
    {
        QrCode.Decode(Render(QrCode.Encode(Link), scale: 3, canvas: 400, offset: 20, inverted: true)).ShouldBe(Link);
    }

    [Fact]
    public void Picture_without_a_code_gives_nothing()
    {
        QrCode.Decode(new ImagePixels(300, 200, Enumerable.Repeat((byte)0xEE, 300 * 200 * 4).ToArray())).ShouldBeNull();
        QrCode.Decode(new ImagePixels(10, 10, new byte[12])).ShouldBeNull("размер буфера не сходится с картинкой");
    }

    [Fact]
    public void Huge_picture_is_not_read_at_all()
    {
        // Больше 40 млн пикселей — даже с кодом картинка не разбирается: память дороже.
        QrCode.Decode(Render(QrCode.Encode(Link), scale: 4, canvas: 6400, offset: 10)).ShouldBeNull();
    }

    private static ImagePixels Render(bool[,] modules, int scale, int canvas, int offset, bool inverted = false)
    {
        var (dark, light) = inverted ? ((byte)0xF0, (byte)0x10) : ((byte)0x10, (byte)0xF0);
        var pixels = new byte[canvas * canvas * 4];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (i % 4) == 3 ? (byte)0xFF : light;
        }

        for (var y = 0; y < modules.GetLength(0); y++)
        {
            for (var x = 0; x < modules.GetLength(1); x++)
            {
                if (!modules[y, x])
                {
                    continue;
                }

                // Белое поле вокруг кода — 4 модуля.
                for (var dy = 0; dy < scale; dy++)
                {
                    for (var dx = 0; dx < scale; dx++)
                    {
                        var at = (((offset + (y + 4) * scale + dy) * canvas) + offset + (x + 4) * scale + dx) * 4;
                        pixels[at] = pixels[at + 1] = pixels[at + 2] = dark;
                    }
                }
            }
        }

        return new ImagePixels(canvas, canvas, pixels);
    }

    private static string FindRoot()
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
