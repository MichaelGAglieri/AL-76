import AppKit
import Foundation

/// Diagrams for studying: "fammi lo schema delle forze del pendolo semplice" (16 November: Claude
/// answered it had no tool to draw). Claude writes the drawing as SVG, which it does well, and the
/// steps of the reasoning as text; here they become one page, saved in "~/Desktop/Schemi JARVIS"
/// and opened in the browser. Nothing goes on the internet: no scripts, no remote images or fonts.
enum DiagramTool {
    static let defaultFolder = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Desktop/Schemi AL-76", isDirectory: true)

    static func draw(_ args: [String: Any]) -> String {
        let title = (args["title"] as? String ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        guard let raw = args["svg"] as? String, let svg = sanitized(raw) else {
            return "Serve un disegno SVG completo, da <svg …> a </svg>."
        }
        let explanation = args["explanation"] as? String ?? ""
        let home = FileManager.default.homeDirectoryForCurrentUser.standardizedFileURL.path
        let folder = (args["folder"] as? String).map(FolderNames.resolve) ?? defaultFolder
        guard folder.standardizedFileURL.path.hasPrefix(home + "/") else { return "Lavoro solo nella tua cartella Inizio." }
        do { try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true) }
        catch { return "Non riesco a creare la cartella \(folder.path): \(error.localizedDescription)" }

        let base = ((args["name"] as? String) ?? (title.isEmpty ? "Schema" : title))
            .replacingOccurrences(of: "/", with: "-").replacingOccurrences(of: ":", with: " -")
            .trimmingCharacters(in: .whitespaces)
        var file = folder.appendingPathComponent(base + ".html")
        // A new drawing never overwrites an old one: "Pendolo semplice 2.html".
        var n = 2
        while FileManager.default.fileExists(atPath: file.path) {
            file = folder.appendingPathComponent("\(base) \(n).html")
            n += 1
        }
        do {
            try page(title: title.isEmpty ? base : title, svg: svg, explanation: explanation)
                .write(to: file, atomically: true, encoding: .utf8)
        } catch {
            return "Non riesco a salvare lo schema: \(error.localizedDescription)"
        }
        NSWorkspace.shared.open(file)
        return "Schema aperto nel browser e salvato in \(file.path.replacingOccurrences(of: home, with: "~"))."
    }

    /// The SVG with anything that could run or fetch removed: scripts, embedded HTML, event
    /// handlers, links outside the drawing. nil if it is not an SVG.
    static func sanitized(_ raw: String) -> String? {
        var svg = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        // Claude sometimes wraps it in a code fence.
        if svg.hasPrefix("```") {
            svg = svg.components(separatedBy: "\n").dropFirst().filter { !$0.hasPrefix("```") }.joined(separator: "\n")
        }
        guard let start = svg.range(of: "<svg", options: .caseInsensitive),
              let end = svg.range(of: "</svg>", options: [.caseInsensitive, .backwards]),
              start.lowerBound < end.lowerBound, svg.utf8.count < 400_000 else { return nil }
        svg = String(svg[start.lowerBound..<end.upperBound])
        let rules: [(String, String)] = [
            (#"(?is)<script\b.*?</script\s*>"#, ""),
            (#"(?is)<script\b[^>]*/?>"#, ""),
            (#"(?is)<foreignObject\b.*?</foreignObject\s*>"#, ""),
            (#"(?is)<(iframe|object|embed|image|use)\b[^>]*?(/>|>.*?</\1\s*>)"#, ""),
            (#"(?i)\son[a-z]+\s*=\s*("[^"]*"|'[^']*'|[^\s>]+)"#, ""),
            // Only links inside the drawing (markers, gradients: "#arrow") stay.
            (#"(?i)\s(xlink:)?href\s*=\s*("[^#"][^"]*"|'[^#'][^']*')"#, ""),
            (#"(?i)url\(\s*['"]?(?!#)[^)]*\)"#, "none"),
            (#"(?i)@import[^;]*;"#, ""),
        ]
        for (pattern, replacement) in rules {
            svg = svg.replacingOccurrences(of: pattern, with: replacement, options: .regularExpression)
        }
        return svg
    }

    private static func escaped(_ text: String) -> String {
        text.replacingOccurrences(of: "&", with: "&amp;").replacingOccurrences(of: "<", with: "&lt;")
            .replacingOccurrences(of: ">", with: "&gt;")
    }

    /// "# " and "## " headings, "- " bullets, blank lines between paragraphs; a line that starts
    /// with "= " is a formula, set apart in a box.
    static func html(_ explanation: String) -> String {
        var out: [String] = []
        var list = false
        func closeList() { if list { out.append("</ul>"); list = false } }
        for line in explanation.components(separatedBy: "\n") {
            let t = line.trimmingCharacters(in: .whitespaces)
            if t.hasPrefix("- ") || t.hasPrefix("• ") {
                if !list { out.append("<ul>"); list = true }
                out.append("<li>\(escaped(String(t.dropFirst(2))))</li>")
                continue
            }
            closeList()
            if t.isEmpty { continue }
            if t.hasPrefix("## ") { out.append("<h3>\(escaped(String(t.dropFirst(3))))</h3>") }
            else if t.hasPrefix("# ") { out.append("<h2>\(escaped(String(t.dropFirst(2))))</h2>") }
            else if t.hasPrefix("= ") { out.append("<div class=\"f\">\(escaped(String(t.dropFirst(2))))</div>") }
            else { out.append("<p>\(escaped(t))</p>") }
        }
        closeList()
        return out.joined(separator: "\n")
    }

    static func page(title: String, svg: String, explanation: String) -> String {
        """
        <!doctype html>
        <html lang="it"><head><meta charset="utf-8">
        <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src data:">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>\(escaped(title))</title>
        <style>
        :root { --ink:#1b2430; --muted:#5b6675; --line:#d9dee5; --paper:#ffffff; --page:#f3f5f8; --accent:#0b6fb8; }
        * { box-sizing:border-box }
        body { margin:0; background:var(--page); color:var(--ink); font:16px/1.55 -apple-system, "SF Pro Text", "Helvetica Neue", sans-serif }
        header { padding:22px 28px 6px } h1 { margin:0; font-size:24px } .sub { color:var(--muted); font-size:13px }
        main { display:grid; grid-template-columns:minmax(0,1.25fr) minmax(300px,1fr); gap:20px; padding:16px 28px 32px; align-items:start }
        .card { background:var(--paper); border:1px solid var(--line); border-radius:12px; padding:18px }
        .draw svg { width:100%; height:auto; display:block }
        .text h2 { font-size:18px; margin:18px 0 6px } .text h3 { font-size:16px; margin:14px 0 4px; color:var(--accent) }
        .text h2:first-child, .text h3:first-child { margin-top:0 }
        .text p { margin:6px 0 } .text ul { margin:6px 0; padding-left:20px }
        .f { font:17px/1.4 "SF Mono", Menlo, monospace; background:#eef4fa; border-left:3px solid var(--accent); padding:8px 12px; margin:8px 0; border-radius:4px; white-space:pre-wrap }
        @media (max-width: 900px) { main { grid-template-columns:1fr } }
        @media print { body { background:#fff } main { grid-template-columns:1fr } .card { border:none } }
        </style></head>
        <body>
        <header><h1>\(escaped(title))</h1><div class="sub">Schema di JARVIS · \(Date().formatted(date: .long, time: .shortened))</div></header>
        <main>
        <section class="card draw">\(svg)</section>
        <section class="card text">\(html(explanation))</section>
        </main>
        </body></html>
        """
    }
}
