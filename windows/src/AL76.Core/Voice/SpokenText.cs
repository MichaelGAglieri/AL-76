using System.Text.RegularExpressions;

namespace AL76.Core.Voice;

/// <summary>
/// Text as it should be said: "AL-76" came out of Kokoro as «LL76», spelled (Mac test, 16/11/2026).
/// Applied by every voice just before synthesis; the text shown on screen keeps the real spelling.
/// </summary>
public static partial class SpokenText
{
    public static string Prepare(string text, string language) =>
        AlName().Replace(text, language == "it" ? "Al settantasei" : "Al seventy-six");

    [GeneratedRegex(@"\bAL[- ]?76\b", RegexOptions.IgnoreCase)]
    private static partial Regex AlName();
}
