using System.Text.Json;

namespace AL76.Core.Localization;

/// <summary>
/// Interface texts, kept out of the code in locales/it.json and locales/en.json (embedded), so other languages
/// can be added later. A missing key shows the key itself, which the tests catch.
/// </summary>
public sealed class Strings
{
    public static readonly string[] Languages = ["it", "en"];

    public string Language { get; }
    private readonly Dictionary<string, string> texts;

    public Strings(string language)
    {
        Language = Languages.Contains(language) ? language : "en";
        texts = Load(Language);
    }

    public string this[string key] => texts.TryGetValue(key, out var text) ? text : key;

    public string Format(string key, params object[] args) => string.Format(this[key], args);

    public IReadOnlyCollection<string> Keys => texts.Keys;

    public static Dictionary<string, string> Load(string language)
    {
        using var stream = typeof(Strings).Assembly.GetManifestResourceStream($"locales.{language}.json")
            ?? throw new InvalidOperationException($"locales/{language}.json missing");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }
}
