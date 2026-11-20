using System.Globalization;
using System.Text;

namespace AL76.Core.Voice;

/// <summary>Small text helpers shared by the wake phrase and the commands.</summary>
public static class TextTools
{
    /// <summary>Lower case, no accents, letters and digits only ("Ehi," → "ehi", "è" → "e").</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>The phrase as normalized words, joined by single spaces.</summary>
    public static string NormalizePhrase(string s) =>
        string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Normalize).Where(w => w.Length > 0));

    public static string[] Words(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Edit distance (Levenshtein), for names Whisper writes slightly differently.</summary>
    public static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            var row = new int[b.Length + 1];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++)
                row[j] = Math.Min(Math.Min(prev[j] + 1, row[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            prev = row;
        }
        return prev[b.Length];
    }
}
