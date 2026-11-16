import Foundation

/// Help with the code open in VS Code (11 November: "guarda il codice che sto scrivendo", "riformattalo e
/// commentalo senza cambiare l'indentazione"; JARVIS could only take a screenshot).
/// It reads the file itself, and changes it only when asked: a copy goes first to
/// ~/Library/Application Support/JARVIS/backup_codice, and in "comments" mode every line of code must stay
/// exactly as it was, indentation included.
enum CodeHelper {
    private static let codeExtensions: Set<String> = ["py", "swift", "c", "cpp", "cc", "h", "hpp", "ino", "js", "ts", "java", "kt",
                                                      "rs", "go", "m", "sh", "zsh", "rb", "lua", "yaml", "yml", "xml", "launch", "json", "toml", "cmake", "txt", "md"]
    private static let hashComments: Set<String> = ["py", "sh", "zsh", "rb", "yaml", "yml", "toml", "cmake", "txt"]
    static let backupFolder = JarvisConfig.directory.appendingPathComponent("backup_codice")

    struct ActiveFile {
        let url: URL
        let unsaved: Bool
    }

    // MARK: Which file

    /// The file in the front VS Code window: its title gives the name ("● " when unsaved), VS Code's own
    /// state gives the folder. Without a title, the most recently changed code file of that folder.
    static func activeFile() async -> ActiveFile? {
        let title = await Shell.osascript("""
            tell application "System Events"
                set p to (first process whose bundle identifier is "com.microsoft.VSCode")
                return name of front window of p
            end tell
            """, timeout: 5)
        let raw = title.ok ? title.out : ""
        let unsaved = raw.hasPrefix("●")
        let name = raw.replacingOccurrences(of: "●", with: "").components(separatedBy: " — ").first?
            .components(separatedBy: " - ").first?.trimmingCharacters(in: .whitespaces) ?? ""
        let folders = vscodeFolders()
        var candidates: [URL] = []
        for folder in folders {
            guard let walker = FileManager.default.enumerator(at: folder, includingPropertiesForKeys: [.contentModificationDateKey],
                                                              options: [.skipsHiddenFiles, .skipsPackageDescendants]) else { continue }
            while let url = walker.nextObject() as? URL {
                if ["node_modules", "build", ".venv", "venv", "__pycache__", "install", "log"].contains(url.lastPathComponent) {
                    walker.skipDescendants()
                    continue
                }
                guard codeExtensions.contains(url.pathExtension.lowercased()) else { continue }
                if name.isEmpty || url.lastPathComponent == name { candidates.append(url) }
                if candidates.count > 400 || walker.level > 5 { if walker.level > 5 { walker.skipDescendants() }; if candidates.count > 400 { break } }
            }
        }
        let newest = candidates.max { modified($0) < modified($1) }
        return newest.map { ActiveFile(url: $0, unsaved: unsaved && (name.isEmpty || $0.lastPathComponent == name)) }
    }

    private static func modified(_ url: URL) -> Date {
        (try? url.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate) ?? .distantPast
    }

    /// Folders open in VS Code, from its storage.json (the last active window first).
    private static func vscodeFolders() -> [URL] {
        let file = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent("Library/Application Support/Code/User/globalStorage/storage.json")
        guard let data = try? Data(contentsOf: file),
              let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let windows = json["windowsState"] as? [String: Any] else { return [] }
        let list: [Any] = [windows["lastActiveWindow"]].compactMap { $0 } + ((windows["openedWindows"] as? [Any]) ?? [])
        return list.compactMap { ($0 as? [String: Any])?["folder"] as? String }.compactMap(URL.init(string:))
            .filter { $0.isFileURL }
    }

    private static func resolve(_ path: String?) async -> (url: URL?, unsaved: Bool, problem: String?) {
        if let path, !path.isEmpty {
            let url = URL(fileURLWithPath: (path as NSString).expandingTildeInPath)
            return (url, false, nil)
        }
        guard let active = await activeFile() else {
            return (nil, false, "Non trovo il file aperto in VS Code: è aperto? Dimmi il nome del file.")
        }
        return (active.url, active.unsaved, nil)
    }

    private static func allowed(_ url: URL) -> String? {
        let home = FileManager.default.homeDirectoryForCurrentUser.standardizedFileURL.path
        let path = url.standardizedFileURL.resolvingSymlinksInPath().path
        guard path.hasPrefix(home + "/"), HomeAccess.allowed(path) else { return "Lavoro solo sui file di codice nella tua cartella Inizio, non nelle cartelle di sistema." }
        guard codeExtensions.contains(url.pathExtension.lowercased()) else { return "\(url.lastPathComponent) non è un file di codice." }
        return nil
    }

    // MARK: Read

    static func read(path: String?, from: Int?, to: Int?) async -> String {
        let (url, unsaved, problem) = await resolve(path)
        guard let url else { return problem ?? "File non trovato." }
        if let refusal = allowed(url) { return refusal }
        guard let text = try? String(contentsOf: url, encoding: .utf8) else { return "Non riesco a leggere \(url.path)." }
        let lines = text.components(separatedBy: "\n")
        let first = max(1, from ?? 1), last = min(lines.count, to ?? lines.count, first + 599)
        guard first <= last else { return "\(url.lastPathComponent) ha \(lines.count) righe." }
        let body = (first...last).map { String(format: "%4d  ", $0) + lines[$0 - 1] }.joined(separator: "\n")
        var head = "File \(url.path), \(lines.count) righe"
        if last < lines.count || first > 1 { head += ", mostrate \(first)–\(last)" }
        if unsaved { head += ". ATTENZIONE: in VS Code ha modifiche non salvate, qui c'è la versione salvata" }
        return Security.external("codice", head + ":\n" + body)
    }

    // MARK: Change

    enum Mode: String { case comments, rewrite }

    static func edit(path: String?, content: String, mode: Mode) async -> String {
        let (url, unsaved, problem) = await resolve(path)
        guard let url else { return problem ?? "File non trovato." }
        if let refusal = allowed(url) { return refusal }
        guard !unsaved else { return "\(url.lastPathComponent) ha modifiche non salvate in VS Code: chiedi all'utente di salvare (Cmd+S), poi rileggi il file." }
        guard let old = try? String(contentsOf: url, encoding: .utf8) else { return "Non riesco a leggere \(url.path)." }
        var new = content.replacingOccurrences(of: "\r\n", with: "\n")
        if old.hasSuffix("\n"), !new.hasSuffix("\n") { new += "\n" }
        guard new != old else { return "Nessuna differenza: il file resta com'è." }
        let ext = url.pathExtension.lowercased()
        if mode == .comments {
            let before = codeLines(old, ext: ext), after = codeLines(new, ext: ext)
            if before != after {
                let index = (0..<min(before.count, after.count)).first { before[$0] != after[$0] } ?? min(before.count, after.count)
                let line = index < before.count ? before[index] : "(fine del file)"
                return "Rifiutato: in modalità commenti il codice deve restare identico, indentazione compresa, ma cambia vicino a \"\(line.trimmingCharacters(in: .whitespaces).prefix(60))\". Rileggi il file e aggiungi solo commenti."
            }
        }
        if ext == "py", let error = await pythonSyntaxError(new) {
            return "Rifiutato: il nuovo codice Python non è valido (\(error)). Il file non è stato toccato."
        }
        // Copy first, then write in one go: VS Code reloads a saved file by itself.
        let stamp = Date().formatted(.iso8601.year().month().day().time(includingFractionalSeconds: false)).replacingOccurrences(of: ":", with: ".")
        let backup = backupFolder.appendingPathComponent("\(stamp) \(url.lastPathComponent)")
        do {
            try FileManager.default.createDirectory(at: backupFolder, withIntermediateDirectories: true)
            try old.write(to: backup, atomically: true, encoding: .utf8)
            let attributes = try? FileManager.default.attributesOfItem(atPath: url.path)
            try new.write(to: url, atomically: true, encoding: .utf8)
            if let permissions = attributes?[.posixPermissions] { try? FileManager.default.setAttributes([.posixPermissions: permissions], ofItemAtPath: url.path) }
        } catch {
            return "Scrittura non riuscita: \(error.localizedDescription). Il file non è stato toccato."
        }
        let delta = new.components(separatedBy: "\n").count - old.components(separatedBy: "\n").count
        return "Salvato \(url.lastPathComponent) (\(mode == .comments ? "solo commenti" : "riscritto"), \(delta >= 0 ? "+" : "")\(delta) righe). Copia di prima: \(backup.lastPathComponent). Per tornare indietro: restore_code."
    }

    /// Puts back the last copy of that file (or of the file open in VS Code).
    static func restore(path: String?) async -> String {
        let (url, _, problem) = await resolve(path)
        guard let url else { return problem ?? "File non trovato." }
        if let refusal = allowed(url) { return refusal }
        let copies = ((try? FileManager.default.contentsOfDirectory(atPath: backupFolder.path)) ?? [])
            .filter { $0.hasSuffix(" " + url.lastPathComponent) }.sorted()
        guard let last = copies.last, let text = try? String(contentsOf: backupFolder.appendingPathComponent(last), encoding: .utf8) else {
            return "Non ho copie di \(url.lastPathComponent)."
        }
        do { try text.write(to: url, atomically: true, encoding: .utf8) } catch { return "Ripristino non riuscito: \(error.localizedDescription)" }
        try? FileManager.default.removeItem(at: backupFolder.appendingPathComponent(last))
        return "Rimesso \(url.lastPathComponent) com'era prima dell'ultima modifica."
    }

    // MARK: Checks

    /// The lines that are code, exactly as written (indentation included): comments and blank lines removed.
    static func codeLines(_ text: String, ext: String) -> [String] {
        let marker = hashComments.contains(ext) ? "#" : "//"
        return text.components(separatedBy: "\n").compactMap { line in
            var code = withoutComment(line, marker: marker)
            while code.last == " " || code.last == "\t" { code.removeLast() }
            return code.trimmingCharacters(in: .whitespaces).isEmpty ? nil : code
        }
    }

    /// The line up to its comment, ignoring markers inside quotes.
    private static func withoutComment(_ line: String, marker: String) -> String {
        var quote: Character? = nil
        var escaped = false
        var index = line.startIndex
        while index < line.endIndex {
            let c = line[index]
            if escaped { escaped = false }
            else if c == "\\" { escaped = true }
            else if let q = quote { if c == q { quote = nil } }
            else if c == "\"" || c == "'" { quote = c }
            else if line[index...].hasPrefix(marker) { return String(line[..<index]) }
            index = line.index(after: index)
        }
        return line
    }

    private static func pythonSyntaxError(_ code: String) async -> String? {
        let temp = FileManager.default.temporaryDirectory.appendingPathComponent("jarvis-check-\(UUID().uuidString).py")
        guard (try? code.write(to: temp, atomically: true, encoding: .utf8)) != nil else { return nil }
        defer { try? FileManager.default.removeItem(at: temp) }
        let result = await Shell.run("/usr/bin/python3", ["-I", "-c", "import ast,sys; ast.parse(open(sys.argv[1], encoding='utf-8').read())", temp.path], timeout: 15)
        guard !result.ok else { return nil }
        return result.err.split(separator: "\n").last.map(String.init) ?? "errore di sintassi"
    }
}
