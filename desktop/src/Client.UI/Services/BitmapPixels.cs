using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PrivateGateway.Client.Application.Import;

namespace PrivateGateway.Client.UI.Services;

/// <summary>Картинка Avalonia — в пиксели BGRA для распознавания QR-кода, в каком бы формате она ни пришла.</summary>
public static class BitmapPixels
{
    public static ImagePixels From(Bitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        var size = bitmap.PixelSize;
        if ((long)size.Width * size.Height > QrCode.MaxPixels)
        {
            // Пустой буфер — распознавание откажет сразу, память не занимается.
            return new ImagePixels(size.Width, size.Height, []);
        }

        using var target = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var locked = target.Lock();
        bitmap.CopyPixels(locked);
        var row = size.Width * 4;
        var pixels = new byte[row * size.Height];
        for (var y = 0; y < size.Height; y++)
        {
            Marshal.Copy(locked.Address + (y * locked.RowBytes), pixels, y * row, row);
        }

        return new ImagePixels(size.Width, size.Height, pixels);
    }

    /// <summary>Картинка из файла; не картинка — <see cref="InvalidDataException"/>.</summary>
    public static ImagePixels Read(Stream stream)
    {
        try
        {
            using var bitmap = new Bitmap(stream);
            return From(bitmap);
        }
        catch (Exception e) when (e is not InvalidDataException and not OutOfMemoryException)
        {
            throw new InvalidDataException("Файл не картинка", e);
        }
    }
}
