using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace PrivateGateway.Client.UI.Controls;

/// <summary>Полный флаг 4:3 в квадратном слоте. Без кода — круглая Земля; без сетевых загрузок или эмодзи шрифта.</summary>
public sealed partial class CountryFlag : UserControl
{
    public static readonly StyledProperty<string?> CountryCodeProperty = AvaloniaProperty.Register<CountryFlag, string?>(nameof(CountryCode));
    private static readonly Dictionary<string, Bitmap> Images = new(StringComparer.Ordinal);
    private static readonly HashSet<string> ResourceNames = [.. typeof(CountryFlag).Assembly.GetManifestResourceNames()];

    public CountryFlag()
    {
        InitializeComponent();
        UpdateImage();
    }

    public string? CountryCode
    {
        get => GetValue(CountryCodeProperty);
        set => SetValue(CountryCodeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CountryCodeProperty)
        {
            UpdateImage();
        }
    }

    private void UpdateImage()
    {
        if (Frame is not null)
        {
            // Флаг не обрезается до квадрата: рисунок 4:3 и такая же рамка по центру слота. Земля остается квадратной.
            var image = ImageFor(CountryCode);
            Frame.Classes.Set("unknown", ReferenceEquals(image, ImageFor(null)));
            Frame.Background = new ImageBrush(image) { Stretch = Stretch.Uniform };
        }
    }

    public static Bitmap ImageFor(string? code)
    {
        var key = code is { Length: 2 } && code.All(char.IsAsciiLetter) ? code.ToUpperInvariant() : "UNKNOWN";
        if (!ResourceNames.Contains("flags." + key + ".png"))
        {
            key = "UNKNOWN";
        }

        lock (Images)
        {
            if (!Images.TryGetValue(key, out var image))
            {
                using var source = typeof(CountryFlag).Assembly.GetManifestResourceStream("flags." + key + ".png")
                    ?? throw new InvalidOperationException("Отсутствует встроенное изображение страны");
                image = new Bitmap(source);
                Images.Add(key, image);
            }

            return image;
        }
    }

}
