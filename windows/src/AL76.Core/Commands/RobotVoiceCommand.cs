using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AL76.Core.Commands;

/// <summary>
/// The robot's whole-phrase voice commands (Mac: RobotVoiceCommand). On Windows Robot Console is reached through its
/// public API, so the commands of the Mac's private channel (enable, single joint, pose sequence, close) are not
/// here: "torna a home" prepares and sends the HOME program (the operator approves it in Robot Console), and while
/// the arm moves a short phrase with a stop word stops it, without the call.
/// </summary>
public static class RobotVoiceCommand
{
    // "Jarvis torna a Dom": Whisper's spelling of "torna a home" on the Mac, 10 November.
    private const string HomeVerbs = "(torna|tornare|ritorna|vai|riporta il robot|riporta il braccio|riportati|porta il robot|porta il braccio)";
    private static readonly Regex Home = new("^" + HomeVerbs + "( a| in| alla| al| nella)? (home|hom|om|ohm|dom|posizione home|posizione iniziale)$"
        + "|^(?:robot |arm )?(?:go |return )?(?:back )?home$|^(?:send|bring) the (?:robot|arm) home$");

    /// <summary>"Al, torna a home", "riporta il braccio in posizione iniziale" (the call already taken away).</summary>
    public static bool IsHome(string raw) => Home.IsMatch(DirectCommand.Clean(raw));

    private static readonly HashSet<string> Stops =
    [
        "stop", "stopp", "stoppa", "stoppati", "basta", "ferma", "fermati", "fermo", "fermalo", "fermatevi", "alt", "halt",
        "arresta", "arrestati", "blocca", "bloccati", "emergenza", "enough",
    ];

    private static string Fold(string s)
    {
        var d = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        return new string(d.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    }

    private static string[] Words(string s) => Regex.Split(Fold(s), @"[^\p{L}]+").Where(w => w.Length > 0).ToArray();

    /// <summary>While the arm moves, any short phrase with one of these words stops it. Whole words only;
    /// "non fermarti" and long phrases are not a STOP. <paramref name="echo"/>: what AL-76 is saying now, so its own
    /// voice from the speakers does not count.</summary>
    public static bool IsMotionStop(string raw, string? echo = null)
    {
        var words = Words(raw);
        if (words.Length == 0 || words.Length > 8 || words.Contains("non") || words.Contains("don")) return false;
        var echoed = Words(echo ?? "").ToHashSet();
        return words.Any(w => Stops.Contains(w) && !echoed.Contains(w));
    }
}
