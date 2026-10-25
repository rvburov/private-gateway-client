using ZXing;
using ZXing.Common;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace PrivateGateway.Client.Application.Import;

/// <summary>
/// Картинка в пикселях BGRA по строкам без промежутков — так интерфейс отдает снимок из буфера,
/// картинку из файла или перетащенную.
/// </summary>
public sealed record ImagePixels(int Width, int Height, byte[] Bgra);

/// <summary>
/// QR-код: ссылка сервера в модули для «Поделиться» и текст с картинки для импорта. ZXing.Net
/// (Apache-2.0), только вычисления — ни сети, ни файлов.
/// </summary>
public static class QrCode
{
    /// <summary>Больше — не снимок экрана: картинку не разбираем, чтобы не занять память.</summary>
    public const int MaxPixels = 40_000_000;

    /// <summary>Модули QR-кода без белого поля: [строка, столбец], true — темный.</summary>
    public static bool[,] Encode(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        var matrix = new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 0, 0, new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.CHARACTER_SET] = "UTF-8",
            [EncodeHintType.ERROR_CORRECTION] = ErrorCorrectionLevel.M,
            [EncodeHintType.MARGIN] = 0,
        });
        var modules = new bool[matrix.Height, matrix.Width];
        for (var y = 0; y < matrix.Height; y++)
        {
            for (var x = 0; x < matrix.Width; x++)
            {
                modules[y, x] = matrix[x, y];
            }
        }

        return modules;
    }

    /// <summary>Текст первого QR-кода на картинке; null — кода нет или картинка не годится.</summary>
    public static string? Decode(ImagePixels image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0 || (long)image.Width * image.Height > MaxPixels
            || image.Bgra.Length != image.Width * image.Height * 4)
        {
            return null;
        }

        var source = new RGBLuminanceSource(image.Bgra, image.Width, image.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
        var hints = new Dictionary<DecodeHintType, object>
        {
            [DecodeHintType.TRY_HARDER] = true,
            [DecodeHintType.CHARACTER_SET] = "UTF-8",
        };
        var reader = new QRCodeReader();
        // Сначала как есть, потом инвертированно: QR в темной теме бывает светлым на темном.
        var result = reader.decode(new BinaryBitmap(new HybridBinarizer(source)), hints)
            ?? reader.decode(new BinaryBitmap(new HybridBinarizer(source.invert())), hints);
        return string.IsNullOrEmpty(result?.Text) ? null : result.Text;
    }
}
