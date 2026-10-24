using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;

namespace PrivateGateway.Client.UI.Localization;

/// <summary>
/// Подпись из словаря в разметке: <c>Text="{l:L nav.home}"</c>. Привязка к
/// <see cref="Loc.Revision"/> с переводом ключа в текст: при смене языка номер растет, и все
/// подписи пересчитываются (привязку к индексатору Avalonia на ходу не обновляет).
/// </summary>
public sealed class LExtension(string key) : MarkupExtension
{
    public string Key { get; } = key;

    /// <summary>Заглавными буквами — для надписей-рубрик (класс overline): в Avalonia нет text-transform.</summary>
    public bool Upper { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new ReflectionBinding(nameof(Loc.Revision))
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay,
            Converter = new FuncValueConverter<int, string>(_ =>
                Upper ? Loc.Instance[Key].ToUpper(Loc.Instance.Culture) : Loc.Instance[Key]),
        };
}
