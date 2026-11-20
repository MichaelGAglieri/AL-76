using System.Globalization;
using System.Text.RegularExpressions;
using static AL76.Core.Tools.ToolDef;

namespace AL76.Core.Tools;

/// <summary>
/// Diagrams for studying (Mac, 16 November: "fammi lo schema delle forze del pendolo semplice"). The brain writes the
/// drawing as SVG and the steps as text; here they become one page, saved in Desktop\Schemi AL-76 and opened in the
/// browser. Nothing goes on the internet: scripts, embedded HTML, event handlers and outside links are removed,
/// and the page forbids loading anything.
/// </summary>
public static class DiagramTool
{
    public static string DefaultFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Schemi AL-76");

    public static ToolDef Create(IPcActions pc, FileSearch search) => new()
    {
        Name = "draw_diagram",
        Description = "Draw a diagram and open it on the PC (\"crea uno schema\", \"disegnami\", \"fammi un'immagine delle forze\"): physics (forces, free-body diagrams, "
                      + "vectors and their components, angles, circuits), robotics (frames, joints, kinematics), flows, anything a student draws. YOU write the drawing as SVG "
                      + "and the explanation as text; it is saved in Desktop\\Schemi AL-76 and opened in the browser. SVG rules: one <svg> with viewBox (about 0 0 760 620), "
                      + "white background, no scripts, no external images or fonts; arrows with <marker>; one colour per force or vector, each labelled next to its tip "
                      + "(e.g. T, mg, mg sinθ, mg cosθ); components dashed; angles with an arc and the symbol; axes of the chosen reference drawn and named; a small legend. "
                      + "Explanation: steps from the drawing to the formula (which axis, which projection, why sin or cos, the sign), then the final equations; '# '/'## ' "
                      + "headings, '- ' bullets, a line starting '= ' is a formula in its own box. Write formulas in plain Unicode (θ, ², √, ·, −), no LaTeX. After the call "
                      + "say in one or two sentences what the drawing shows; do not read the whole explanation aloud.",
        Properties = new()
        {
            ["title"] = Prop("string", "Title, e.g. Pendolo semplice: forze."),
            ["svg"] = Prop("string", "The complete SVG, from <svg ...> to </svg>."),
            ["explanation"] = Prop("string", "Step-by-step explanation shown next to the drawing."),
            ["name"] = Prop("string", "File name; default the title."),
            ["folder"] = Prop("string", "Folder; default Desktop\\Schemi AL-76."),
        },
        Required = ["title", "svg", "explanation"],
        Run = async (a, c, ct) =>
        {
            if (Sanitized(a["svg"]?.ToString() ?? "") is not string svg)
                return ToolOutcome.Error(c.Italian ? "Serve un disegno SVG completo, da <svg …> a </svg>." : "A complete SVG drawing is needed, from <svg …> to </svg>.");
            string title = a.Str("title");
            string folder = a.Str("folder").Length > 0 ? FileTools.Expand(a.Str("folder")) : DefaultFolder();
            if (!Path.IsPathRooted(folder)) folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), folder);
            if (!search.Allowed(folder)) return ToolOutcome.Error(c.Italian ? "In quella cartella non scrivo." : "That folder is off limits.");
            string name = DocumentTools.Clean(a.Str("name").Length > 0 ? a.Str("name") : title.Length > 0 ? title : (c.Italian ? "Schema" : "Diagram"));
            try
            {
                Directory.CreateDirectory(folder);
                // A new drawing never overwrites an old one: "Pendolo semplice 2.html".
                string file = Path.Combine(folder, name + ".html");
                for (int n = 2; File.Exists(file); n++) file = Path.Combine(folder, $"{name} {n}.html");
                await File.WriteAllTextAsync(file, Page(title.Length > 0 ? title : name, svg, a["explanation"]?.ToString() ?? "", c.Italian, DateTime.Now), ct);
                await pc.OpenPathAsync(file, false, ct);
                return ToolOutcome.Ok(c.Italian ? $"Schema aperto nel browser e salvato in {file}." : $"Diagram opened in the browser and saved in {file}.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return ToolOutcome.Error(c.Italian ? $"Non riesco a salvare lo schema: {e.Message}" : $"Could not save the diagram: {e.Message}");
            }
        },
    };

    /// <summary>The SVG with anything that could run or fetch removed; null if it is not an SVG.</summary>
    public static string? Sanitized(string raw)
    {
        string svg = raw.Trim();
        if (svg.StartsWith("```")) svg = string.Join("\n", svg.Split('\n').Skip(1).Where(l => !l.StartsWith("```")));
        int start = svg.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
        int end = svg.LastIndexOf("</svg>", StringComparison.OrdinalIgnoreCase);
        if (start < 0 || end < start || svg.Length > 400_000) return null;
        svg = svg[start..(end + 6)];
        (string pattern, string with)[] rules =
        [
            (@"(?is)<script\b.*?</script\s*>", ""),
            (@"(?is)<script\b[^>]*/?>", ""),
            (@"(?is)<foreignObject\b.*?</foreignObject\s*>", ""),
            (@"(?is)<(iframe|object|embed|image|use)\b[^>]*?(/>|>.*?</\1\s*>)", ""),
            (@"(?i)\son[a-z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", ""),
            // Only links inside the drawing (markers, gradients: "#arrow") stay.
            (@"(?i)\s(xlink:)?href\s*=\s*(""[^#""][^""]*""|'[^#'][^']*')", ""),
            (@"(?i)url\(\s*['""]?(?!#)[^)]*\)", "none"),
            (@"(?i)@import[^;]*;", ""),
        ];
        foreach (var (pattern, with) in rules) svg = Regex.Replace(svg, pattern, with);
        return svg;
    }

    /// <summary>Only the characters that matter in HTML: θ, ² and the like stay readable in the file.</summary>
    private static string E(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    /// <summary>"# " and "## " headings, "- " bullets, paragraphs; a line starting "= " is a formula in a box.</summary>
    public static string Html(string explanation)
    {
        var o = new List<string>();
        bool list = false;
        void Close() { if (list) { o.Add("</ul>"); list = false; } }
        foreach (var line in explanation.Replace("\r", "").Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("- ") || t.StartsWith("• "))
            {
                if (!list) { o.Add("<ul>"); list = true; }
                o.Add($"<li>{E(t[2..])}</li>");
                continue;
            }
            Close();
            if (t.Length == 0) continue;
            if (t.StartsWith("## ")) o.Add($"<h3>{E(t[3..])}</h3>");
            else if (t.StartsWith("# ")) o.Add($"<h2>{E(t[2..])}</h2>");
            else if (t.StartsWith("= ")) o.Add($"<div class=\"f\">{E(t[2..])}</div>");
            else o.Add($"<p>{E(t)}</p>");
        }
        Close();
        return string.Join("\n", o);
    }

    public static string Page(string title, string svg, string explanation, bool it, DateTime now)
    {
        string when = now.ToString(it ? "d MMMM yyyy, HH:mm" : "d MMMM yyyy, HH:mm", CultureInfo.GetCultureInfo(it ? "it-IT" : "en-GB"));
        return $$"""
            <!doctype html>
            <html lang="{{(it ? "it" : "en")}}"><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src data:">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{E(title)}}</title>
            <style>
            :root { --ink:#1b2430; --muted:#5b6675; --line:#d9dee5; --paper:#ffffff; --page:#f3f5f8; --accent:#0b6fb8; }
            * { box-sizing:border-box }
            body { margin:0; background:var(--page); color:var(--ink); font:16px/1.55 "Segoe UI", system-ui, sans-serif }
            header { padding:22px 28px 6px } h1 { margin:0; font-size:24px } .sub { color:var(--muted); font-size:13px }
            main { display:grid; grid-template-columns:minmax(0,1.25fr) minmax(300px,1fr); gap:20px; padding:16px 28px 32px; align-items:start }
            .card { background:var(--paper); border:1px solid var(--line); border-radius:12px; padding:18px }
            .draw svg { width:100%; height:auto; display:block }
            .text h2 { font-size:18px; margin:18px 0 6px } .text h3 { font-size:16px; margin:14px 0 4px; color:var(--accent) }
            .text h2:first-child, .text h3:first-child { margin-top:0 }
            .text p { margin:6px 0 } .text ul { margin:6px 0; padding-left:20px }
            .f { font:17px/1.4 Consolas, "Cascadia Mono", monospace; background:#eef4fa; border-left:3px solid var(--accent); padding:8px 12px; margin:8px 0; border-radius:4px; white-space:pre-wrap }
            @media (max-width: 900px) { main { grid-template-columns:1fr } }
            @media print { body { background:#fff } main { grid-template-columns:1fr } .card { border:none } }
            </style></head>
            <body>
            <header><h1>{{E(title)}}</h1><div class="sub">{{(it ? "Schema di AL-76" : "Diagram by AL-76")}} · {{E(when)}}</div></header>
            <main>
            <section class="card draw">{{svg}}</section>
            <section class="card text">{{Html(explanation)}}</section>
            </main>
            </body></html>
            """;
    }
}
