namespace AL76.Core.Voice;

/// <summary>
/// Phrases Whisper invents on noise or silence ("Grazie.", "Sottotitoli a cura di..."): on the Mac they
/// were the "Grazie" fantasma. They are dropped before anything else looks at the text.
/// </summary>
public static class Hallucinations
{
    private static readonly string[] Exact =
    [
        "grazie", "grazie a tutti", "grazie per la visione", "ciao", "amen", "thank you", "thanks for watching",
        "you", "bye", "sottotitoli e revisione a cura di qtss", "sottotitoli creati dalla comunita amaraorg",
    ];
    private static readonly string[] Starts = ["sottotitoli", "subtitles by", "trascrizione a cura"];

    public static bool IsHallucination(string text)
    {
        string n = TextTools.NormalizePhrase(text);
        if (n.Length == 0) return true;
        if (n.Count(char.IsLetter) < 2) return true;
        return Exact.Contains(n) || Starts.Any(n.StartsWith);
    }
}
