using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Styling;
using PrivateGateway.Client.Core;
using PrivateGateway.Client.UI.ViewModels;

namespace PrivateGateway.Client.UI.Converters;

/// <summary>Ресурс темы по ключу; цвета и размеры есть только в Tokens.axaml.</summary>
internal static class ThemeResources
{
    public static object Get(string key) =>
        Avalonia.Application.Current is { } app && app.TryGetResource(key, ThemeVariant.Light, out var value) && value is not null
            ? value
            : throw new KeyNotFoundException($"Нет ресурса темы {key}");
}

/// <summary>
/// Цвет состояния защиты: <c>{Binding Tone, Converter={StaticResource ToneBrush}, ConverterParameter=Ring}</c>.
/// Части: Ring (кольцо), Icon (знак и заголовок), Tint (фон плашки), Fill (круг кнопки при наведении),
/// Glow (тень-свечение кнопки).
/// </summary>
public sealed class ToneBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Tone tone)
        {
            return null;
        }

        return (parameter as string) switch
        {
            "Ring" => ThemeResources.Get($"PgBrush.State.{tone}.Ring"),
            "Icon" => ThemeResources.Get($"PgBrush.State.{tone}.Icon"),
            "Fill" => ThemeResources.Get($"PgBrush.State.{tone}.Fill"),
            "Tint" => ThemeResources.Get(TintKey(tone)),
            "Glow" => new BoxShadows(new BoxShadow
            {
                OffsetY = 4,
                Blur = 12,
                Color = (Color)ThemeResources.Get($"PgColor.State.{tone}.Glow"),
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(parameter), parameter, "Ring, Icon, Tint, Fill или Glow"),
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static string TintKey(Tone tone) => tone switch
    {
        Tone.Connected => "PgBrush.Status.Success.Tint",
        Tone.Connecting or Tone.Reconnecting => "PgBrush.Accent.Tint",
        Tone.Blocked => "PgBrush.Status.Warning.Tint",
        Tone.Partial => "PgBrush.Status.Caution.Tint",
        _ => "PgBrush.Status.Danger.Tint",
    };
}

/// <summary>Цвета чипа проверки: Tint (фон), Hover (фон при наведении и выборе), Text (подпись), Dot (кружок и рамка).</summary>
public sealed class CheckBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not CheckState state)
        {
            return null;
        }

        var palette = state switch
        {
            CheckState.Ok => "Status.Success",
            CheckState.Waiting => "Accent",
            CheckState.Stopped => "Status.Warning",
            CheckState.Warning => "Status.Caution",
            _ => "Status.Danger",
        };
        return (parameter as string) switch
        {
            "Tint" => ThemeResources.Get($"PgBrush.{palette}.Tint"),
            "Hover" => ThemeResources.Get($"PgBrush.{palette}.Hover"),
            "Text" => ThemeResources.Get(palette == "Accent" ? "PgBrush.Accent.Text" : $"PgBrush.{palette}.Text"),
            "Dot" => ThemeResources.Get(palette == "Accent" ? "PgBrush.Accent.Base" : $"PgBrush.{palette}.Base"),
            _ => throw new ArgumentOutOfRangeException(nameof(parameter), parameter, "Tint, Hover, Text или Dot"),
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Иконка по ключу ресурса (Icon.*): модель представления знает только имя.</summary>
public sealed class IconKeyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string key ? ThemeResources.Get(key) : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Рубрика заглавными буквами (overline макета) для текста из привязки, по правилам языка.</summary>
public sealed class UpperConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text ? text.ToUpper(Localization.Loc.Instance.Culture) : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
