using System.Text.Json;
using AL76.Core.Localization;

namespace AL76.Linux.App;

/// <summary>The core's texts plus the few only Linux needs (locales/*.json of this project), same language.</summary>
internal sealed class LinuxStrings(Strings core)
{
    private readonly Dictionary<string, string> own = Load(core.Language);
    public Strings Core { get; } = core;
    public string Language => Core.Language;

    public string this[string key] => own.TryGetValue(key, out var t) ? t : Core[key];
    public string Format(string key, params object[] args) => string.Format(this[key], args);

    private static Dictionary<string, string> Load(string language)
    {
        var assembly = typeof(LinuxStrings).Assembly;
        using var stream = assembly.GetManifestResourceStream($"locales.linux.{language}.json")
                        ?? assembly.GetManifestResourceStream("locales.linux.en.json");
        return stream is null ? [] : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }
}
