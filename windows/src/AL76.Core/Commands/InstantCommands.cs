using AL76.Core.Voice;

namespace AL76.Core.Commands;

/// <summary>
/// Answers that need no model and no internet, in under a second. Phase 1 has only time and date, enough
/// to prove the voice loop; phase 2 ports the whole InstantCommand set of the Mac (apps, volume, timers...).
/// </summary>
public static class InstantCommands
{
    private static readonly string[] TimeIt = ["che ore sono", "che ora e", "che ora sono", "mi dici l ora", "dimmi l ora", "ore sono"];
    private static readonly string[] TimeEn = ["what time is it", "what s the time", "whats the time", "tell me the time"];
    private static readonly string[] DateIt = ["che giorno e", "che giorno e oggi", "che data e", "quanti ne abbiamo", "che giorno siamo"];
    private static readonly string[] DateEn = ["what day is it", "what s the date", "whats the date", "what is the date", "what day is today",
                                                   "what s the day today", "whats the day today", "what is the day today", "today s date", "todays date"];

    /// <summary>The spoken answer, or null if the command is not an instant one.</summary>
    public static string? Answer(string command, string language, DateTime now)
    {
        string n = " " + TextTools.NormalizePhrase(command.Replace('\'', ' ')) + " ";
        bool Has(string[] keys) => keys.Any(k => n.Contains(" " + k + " "));
        bool it = language == "it";
        if (Has(TimeIt) || Has(TimeEn)) return it ? TimeItalian(now) : $"It's {now:h:mm tt}.";
        if (Has(DateIt) || Has(DateEn))
            return it ? $"Oggi è {now.ToString("dddd d MMMM yyyy", new System.Globalization.CultureInfo("it-IT"))}."
                      : $"Today is {now.ToString("dddd, MMMM d, yyyy", new System.Globalization.CultureInfo("en-US"))}.";
        return null;
    }

    /// <summary>"Sono le 22 e 15", "È l'una e 5", "È mezzanotte", "Sono le 8 in punto".</summary>
    public static string TimeItalian(DateTime t)
    {
        string minutes = t.Minute == 0 ? " in punto" : $" e {t.Minute}";
        return t.Hour switch
        {
            0 => t.Minute == 0 ? "È mezzanotte." : $"È mezzanotte{minutes}.",
            12 => t.Minute == 0 ? "È mezzogiorno." : $"È mezzogiorno{minutes}.",
            1 or 13 => $"È l'una{minutes}.",
            _ => $"Sono le {t.Hour}{minutes}.",
        };
    }
}
