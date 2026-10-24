using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace PrivateGateway.Client.UI.Localization;

/// <summary>
/// Тексты интерфейса RU/EN из shared/specifications/localization (встроены в сборку). Язык
/// меняется на ходу: разметка привязана к <see cref="Revision"/>, и смена языка обновляет все подписи.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    /// <summary>Языки интерфейса: код → файл словаря.</summary>
    public static IReadOnlyList<string> Languages { get; } = ["ru", "en"];

    public static Loc Instance { get; } = new();

    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _dictionaries = [];
    private IReadOnlyDictionary<string, string> _current;

    private Loc()
    {
        foreach (var language in Languages)
        {
            _dictionaries[language] = Load(language);
        }

        Language = "ru";
        _current = _dictionaries[Language];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Язык поменялся — модели представлений пересчитывают свои тексты.</summary>
    public event EventHandler? LanguageChanged;

    public string Language { get; private set; }

    /// <summary>
    /// Номер версии словаря: растет при смене языка. На него привязана разметка
    /// (<see cref="LExtension"/>) — так подписи обновляются на ходу.
    /// </summary>
    public int Revision { get; private set; }

    public CultureInfo Culture => CultureInfo.GetCultureInfo(Language);

    /// <summary>Текст по ключу; неизвестный ключ виден сразу — «[ключ]».</summary>
    public string this[string key] => _current.TryGetValue(key, out var text) ? text : "[" + key + "]";

    /// <summary>Есть ли ключ в словаре — для текстов с запасными вариантами (уточнение → состояние → общий).</summary>
    public bool Has(string key) => _current.ContainsKey(key);

    public string Format(string key, params object?[] args) =>
        string.Format(Culture, this[key], args);

    public void SetLanguage(string language)
    {
        if (!_dictionaries.TryGetValue(language, out var dictionary))
        {
            throw new ArgumentOutOfRangeException(nameof(language), language, "Нет такого языка");
        }

        if (language == Language)
        {
            return;
        }

        Language = language;
        _current = dictionary;
        Revision++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Revision)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Словарь языка как есть — для тестов полноты.</summary>
    public IReadOnlyDictionary<string, string> Dictionary(string language) => _dictionaries[language];

    private static Dictionary<string, string> Load(string language)
    {
        var name = $"loc.{language}.json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"В сборке нет словаря {name}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)
            ?? throw new InvalidOperationException($"Пустой словарь {name}");
    }
}
