using System.Text;

namespace AL76.Core.Voice;

/// <summary>
/// Cuts an answer into pieces a voice can say one after the other: the first piece is short, so the voice starts
/// at once while the rest is made. Pieces end at . ! ? ; : or a new line; tiny ones join the next.
/// </summary>
public static class Sentences
{
    public static List<string> Split(string text, int firstMax = 80, int minLength = 12)
    {
        var raw = new List<string>();
        var current = new StringBuilder();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            current.Append(c);
            bool end = c is '.' or '!' or '?' or ';' or ':' or '\n';
            // "16.30", "3.5", "e.g." stay whole: a stop needs a space or the end after it.
            if (end && (i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                raw.Add(current.ToString().Trim());
                current.Clear();
            }
        }
        if (current.ToString().Trim() is { Length: > 0 } rest) raw.Add(rest);
        raw.RemoveAll(p => p.Length == 0);

        var pieces = new List<string>();
        foreach (var p in raw)
        {
            if (pieces.Count > 0 && (pieces[^1].Length < minLength || p.Length < minLength / 2))
                pieces[^1] = pieces[^1] + " " + p;
            else pieces.Add(p);
        }
        // A long first sentence is cut at its first comma, so the first sound is quick.
        if (pieces.Count > 0 && pieces[0].Length > firstMax)
        {
            int comma = pieces[0].IndexOf(", ", minLength, StringComparison.Ordinal);
            if (comma > 0 && comma < pieces[0].Length - minLength)
            {
                string head = pieces[0][..(comma + 1)];
                string tail = pieces[0][(comma + 2)..];
                pieces[0] = head;
                pieces.Insert(1, tail);
            }
        }
        return pieces;
    }
}
