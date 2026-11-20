using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AL76.Core.Commands;

/// <summary>
/// A day and a time at the end (or the start) of a sentence: "domani alle 9", "giovedì alle 15 e 30",
/// "tra 20 minuti", "stasera", "il 20 novembre alle 18", "tomorrow at 9", "in 20 minutes". Ported from the Mac's
/// DirectCommand.When; anything these fixed rules do not read goes to the brain.
/// </summary>
public static class When
{
    public sealed record Moment(DateTime Date, bool HasTime);

    private static readonly string[] Weekdays = ["domenica", "lunedi", "martedi", "mercoledi", "giovedi", "venerdi", "sabato"];
    private static readonly string[] WeekdaysEn = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];
    private static readonly string[] Months = ["gennaio", "febbraio", "marzo", "aprile", "maggio", "giugno", "luglio", "agosto", "settembre", "ottobre", "novembre", "dicembre"];
    private static readonly Dictionary<string, int> Numbers = new()
    {
        ["un"] = 1, ["una"] = 1, ["uno"] = 1, ["due"] = 2, ["tre"] = 3, ["quattro"] = 4, ["cinque"] = 5, ["sei"] = 6, ["sette"] = 7, ["otto"] = 8,
        ["nove"] = 9, ["dieci"] = 10, ["undici"] = 11, ["dodici"] = 12, ["quindici"] = 15, ["venti"] = 20, ["trenta"] = 30, ["quaranta"] = 40,
        ["quarantacinque"] = 45, ["cinquanta"] = 50, ["a"] = 1, ["an"] = 1, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["five"] = 5, ["ten"] = 10,
        ["fifteen"] = 15, ["twenty"] = 20, ["thirty"] = 30,
    };

    private const string Day = @"(?:oggi pomeriggio|domani mattina|domani sera|domani pomeriggio|dopodomani|domattina|domani|oggi|stasera|stamattina|stanotte|tomorrow morning|tomorrow evening|tomorrow|today|tonight|this evening|(?:il |l')?\d{1,2}(?: (?:gennaio|febbraio|marzo|aprile|maggio|giugno|luglio|agosto|settembre|ottobre|novembre|dicembre))?|(?:il |la |di |on )?(?:prossim[oa] |next )?(?:lunedi|martedi|mercoledi|giovedi|venerdi|sabato|domenica|lunedì|martedì|mercoledì|giovedì|venerdì|monday|tuesday|wednesday|thursday|friday|saturday|sunday)(?: prossim[oa])?(?: mattina| sera| pomeriggio| morning| evening| afternoon)?)";
    private const string Time = @"(?:alle|all'|per le|verso le|a|at) ?(?:ore )?(?:\d{1,2}(?:[:.]\d{2})?(?: e (?:\d{1,2}|mezza|mezzo|un quarto|trenta|quindici|quarantacinque))?(?: ?(?:am|pm))?(?: di (?:mattina|sera|pomeriggio|notte)| del (?:mattino|pomeriggio)| in the (?:morning|afternoon|evening))?|una|mezzogiorno|mezzanotte|noon|midnight)";
    private const string Relative = @"(?:tra|fra|in) (?:\d{1,3}|un|una|un'|due|tre|quattro|cinque|dieci|quindici|venti|trenta|quaranta|quarantacinque|mezz'|a|an|one|two|three|five|ten|fifteen|twenty|thirty) ?(?:minuti|minuto|ore|ora|giorni|giorno|minutes|minute|hours|hour|days|day)(?: e mezza| e mezzo| and a half)?";

    private static string Fold(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }

    private static string[]? Match(string text, string pattern)
    {
        var m = Regex.Match(text, "^(?:" + pattern + ")$");
        return m.Success ? m.Groups.Cast<Group>().Select(g => g.Success ? g.Value : "").ToArray() : null;
    }

    /// <summary>
    /// The sentence without its day and time, and the moment they give (null if it names none). Null
    /// altogether when date words are there but in a form these rules do not read: the brain reads it.
    /// </summary>
    public static (string rest, Moment? when)? Split(string text, DateTime now)
    {
        string when = $"(?:{Relative}|{Day}(?: {Time})?|{Time}(?: (?:di )?{Day})?)";
        string rest = text, found = "";
        if (Match(text, $"(.+?) (?:per |for )?({when})") is { } m) { rest = m[1]; found = m[2]; }
        else if (Match(text, $"({when}) (?:di |che devo |che |to )?(.+)") is { } m2) { found = m2[1]; rest = m2[2]; }
        if (found.Length == 0)
        {
            string[] hints = ["prossim", "settimana", "mese", "tra ", "fra ", "alle ", "domani", "stasera", "ogni ", "lunedi", "lunedì", "martedi", "martedì",
                              "mercoledi", "mercoledì", "giovedi", "giovedì", "venerdi", "venerdì", "sabato", "domenica", "tomorrow", "next ", "every ", " at "];
            return hints.Any((" " + text + " ").Contains) ? null : (text, null);
        }
        return MomentOf(found, now) is Moment moment ? (rest.Trim(), moment) : null;
    }

    public static Moment? MomentOf(string raw, DateTime now)
    {
        string text = Fold(raw.ToLowerInvariant());
        if (Match(text, @"(?:tra|fra|in) (\S+?) ?(minuti|minuto|ore|ora|giorni|giorno|minutes|minute|hours|hour|days|day)( e mezza| e mezzo| and a half)?") is { } rel)
        {
            string word = rel[1].Replace("'", "");
            double amount;
            if (double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) amount = d;
            else if (Numbers.TryGetValue(word, out int n)) amount = n;
            else if (word == "mezz") amount = 0.5;
            else return null;
            if (rel[3].Length > 0) amount += 0.5;
            double seconds = rel[2].StartsWith("minut") ? 60 : rel[2].StartsWith("or") || rel[2].StartsWith("hour") ? 3600 : 86400;
            return new Moment(now.AddSeconds(amount * seconds), seconds < 86400);
        }

        // The day.
        DateTime day = now.Date;
        bool dayGiven = true;
        string part = "";
        int weekday = Array.FindIndex(Weekdays, text.Contains);
        if (weekday < 0) weekday = Array.FindIndex(WeekdaysEn, text.Contains);
        if (text.Contains("dopodomani")) day = day.AddDays(2);
        else if (text.Contains("domani") || text.Contains("domattina") || text.Contains("tomorrow")) day = day.AddDays(1);
        else if (weekday >= 0)
        {
            int ahead = (weekday - (int)now.DayOfWeek + 7) % 7;
            day = day.AddDays(ahead == 0 ? 7 : ahead);
        }
        else if (Match(text, @".*?(?:il |l')?(\d{1,2}) (gennaio|febbraio|marzo|aprile|maggio|giugno|luglio|agosto|settembre|ottobre|novembre|dicembre)(?: .*)?") is { } dm)
        {
            int number = int.Parse(dm[1]);
            int month = Array.IndexOf(Months, dm[2]) + 1;
            if (number < 1 || number > DateTime.DaysInMonth(now.Year, month)) return null;
            var date = new DateTime(now.Year, month, number);
            if (date < day) date = date.AddYears(1);
            day = date;
        }
        else if (!new[] { "oggi", "stasera", "stamattina", "stanotte", "today", "tonight", "this evening" }.Any(text.Contains)) dayGiven = false;
        foreach (var w in new[] { "mattina", "stamattina", "domattina", "pomeriggio", "sera", "stasera", "notte", "stanotte", "morning", "afternoon", "evening", "tonight" })
            if (text.Contains(w)) part = w;

        // The time.
        int? hour = null;
        int minute = 0;
        if (Match(text, @".*?(?:alle|all'|per le|verso le|a|at) ?(?:ore )?(\d{1,2})(?:[:.](\d{2}))?(?: e (\d{1,2}|mezza|mezzo|un quarto|trenta|quindici|quarantacinque))? ?(am|pm)?(?: (?:di|del|in the) (mattina|mattino|sera|pomeriggio|notte|morning|afternoon|evening))?(?: .*)?") is { } t)
        {
            hour = int.Parse(t[1]);
            minute = int.TryParse(t[2], out int mm) ? mm : int.TryParse(t[3], out int m3) ? m3
                : t[3] switch { "mezza" or "mezzo" or "trenta" => 30, "un quarto" or "quindici" => 15, "quarantacinque" => 45, _ => 0 };
            if (t[4] == "pm" && hour < 12) hour += 12;
            if (t[4] == "am" && hour == 12) hour = 0;
            if (t[4].Length > 0) part = "am-pm";
            if (t[5].Length > 0) part = t[5];
        }
        else if (text.Contains("all'una") || text.EndsWith(" una") || text.Contains("alle una")) hour = 13;
        else if (text.Contains("mezzogiorno") || text.Contains("noon")) hour = 12;
        else if (text.Contains("mezzanotte") || text.Contains("midnight"))
        {
            hour = 0;
            if (!dayGiven || text.Contains("stanotte")) day = day.AddDays(1);
        }

        if (hour is not int h)
        {
            // Only a part of the day: a usual hour for it.
            if (!dayGiven) return null;
            int? usual = part switch
            {
                "mattina" or "stamattina" or "domattina" or "morning" => 9,
                "pomeriggio" or "afternoon" => 15,
                "sera" or "stasera" or "evening" or "tonight" => 20,
                "notte" or "stanotte" => 22,
                _ => null,
            };
            if (usual is null) return new Moment(day.AddHours(9), false);
            var at = day.AddHours(usual.Value);
            // "Stasera" said at nine in the evening: in an hour, not at eight.
            return new Moment(at > now ? at : now.AddHours(1), true);
        }
        if (h is < 0 or > 23 || minute is < 0 or > 59) return null;
        if (part is "sera" or "stasera" or "pomeriggio" or "notte" or "stanotte" or "afternoon" or "evening" or "tonight" && h < 12
            && !(part.Contains("notte") && h < 6)) h += 12;
        var result = day.AddHours(h).AddMinutes(minute);
        // "Alle 6" said in the afternoon is this evening; a time already past with no day is tomorrow.
        if (result <= now && part.Length == 0 && h < 12 && (!dayGiven || day == now.Date) && day.AddHours(h + 12).AddMinutes(minute) is var later && later > now)
            result = later;
        if (result <= now && !dayGiven) result = result.AddDays(1);
        return result > now.AddMinutes(-1) ? new Moment(result, true) : null;
    }
}
