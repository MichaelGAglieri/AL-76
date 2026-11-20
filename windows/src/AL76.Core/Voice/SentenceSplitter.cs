using System.Text;
using System.Text.RegularExpressions;

namespace AL76.Core.Voice;

/// <summary>
/// Cuts the text a brain streams into complete sentences, to hand each one to the voice at once (the Mac's
/// SentenceSplitter). A sentence ends at . ! ? ; : … followed by a space, or at a new line; one shorter than
/// 12 characters ("Sì.", "Ok.") stays joined to the next. A &lt;think&gt; block is never read aloud.
/// </summary>
public sealed partial class SentenceSplitter
{
    private readonly StringBuilder buffer = new();
    private bool insideThink;

    [GeneratedRegex(@"[.!?;:…]+[""»”')]?\s|\n+")]
    private static partial Regex End();

    public List<string> Push(string chunk)
    {
        buffer.Append(chunk);
        StripThinking();
        var sentences = new List<string>();
        if (insideThink) return sentences;
        int searchStart = 0;
        while (true)
        {
            string text = buffer.ToString();
            var m = End().Match(text, searchStart);
            if (!m.Success) break;
            int end = m.Index + m.Length;
            string sentence = text[..end].Trim();
            if (sentence.Length < 12)
            {
                searchStart = end;
                if (searchStart >= text.Length) break;
                continue;
            }
            sentences.Add(sentence);
            buffer.Remove(0, end);
            searchStart = 0;
        }
        return sentences;
    }

    public string? Flush()
    {
        StripThinking();
        if (insideThink)
        {
            string t = buffer.ToString();
            int start = t.IndexOf("<think>", StringComparison.Ordinal);
            if (start >= 0) buffer.Remove(start, buffer.Length - start);
        }
        insideThink = false;
        string rest = buffer.ToString().Trim();
        buffer.Clear();
        return rest.Length == 0 ? null : rest;
    }

    private void StripThinking()
    {
        while (true)
        {
            string t = buffer.ToString();
            int start = t.IndexOf("<think>", StringComparison.Ordinal);
            if (start < 0) { insideThink = false; return; }
            int end = t.IndexOf("</think>", start, StringComparison.Ordinal);
            if (end < 0) { insideThink = true; return; }
            buffer.Remove(start, end + "</think>".Length - start);
        }
    }
}
