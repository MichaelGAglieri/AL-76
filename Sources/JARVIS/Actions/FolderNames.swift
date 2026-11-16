import Foundation

/// Folder names as they are said or heard: "My Project" is the existing "My_Projects"
/// (12 November: a repository was downloaded into a new, wrong folder).
enum FolderNames {
    static func key(_ name: String) -> String {
        name.lowercased().folding(options: .diacriticInsensitive, locale: nil).filter { $0.isLetter || $0.isNumber }
    }

    /// The same name apart from spaces, underscores, plurals and a letter or two.
    static func alike(_ said: String, _ real: String) -> Bool {
        let a = key(said), b = key(real)
        guard !a.isEmpty, !b.isEmpty else { return false }
        if a == b { return true }
        guard min(a.count, b.count) >= 5, a.first == b.first else { return false }
        return distance(a, b) <= max(2, min(a.count, b.count) / 4)
    }

    /// The path with every missing folder replaced by an existing sibling with a similar name, if there is
    /// exactly one. What really does not exist stays as written, to be created.
    static func resolve(_ path: String) -> URL {
        let url = URL(fileURLWithPath: (path as NSString).expandingTildeInPath).standardizedFileURL
        var current = URL(fileURLWithPath: "/")
        for part in url.pathComponents.dropFirst() {
            let exact = current.appendingPathComponent(part)
            if FileManager.default.fileExists(atPath: exact.path) { current = exact; continue }
            let siblings = ((try? FileManager.default.contentsOfDirectory(at: current, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles])) ?? [])
                .filter { (try? $0.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true && alike(part, $0.lastPathComponent) }
            current = siblings.count == 1 ? siblings[0] : exact
        }
        return current
    }

    /// A folder he names ("apri la cartella appunti corso"), among the usual places and one level below them.
    static func find(_ said: String) -> URL? {
        let home = FileManager.default.homeDirectoryForCurrentUser
        // Also the app data in Library ("apri la cartella mods di minecraft", 13 November).
        let roots = ["Desktop", "Documents", "Downloads", "Projects", "", "Library/Application Support"].map { $0.isEmpty ? home : home.appendingPathComponent($0) }
        func folders(_ root: URL) -> [URL] {
            ((try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: [.isDirectoryKey], options: [.skipsHiddenFiles, .skipsPackageDescendants])) ?? [])
                .filter { (try? $0.resourceValues(forKeys: [.isDirectoryKey]).isDirectory) == true && $0.pathExtension != "app" }
        }
        let special = ["scrivania": "Desktop", "desktop": "Desktop", "documenti": "Documents", "download": "Downloads", "downloads": "Downloads",
                       "scaricati": "Downloads", "progetti": "Projects", "projects": "Projects", "inizio": "", "home": ""]
        if let known = special[said.lowercased()] { return known.isEmpty ? home : home.appendingPathComponent(known) }
        let first = roots.flatMap(folders).filter { $0.path != home.appendingPathComponent("Library").path && HomeAccess.allowed($0.path) }
        var exact = first.filter { key($0.lastPathComponent) == key(said) }
        if exact.isEmpty { exact = first.flatMap(folders).filter { key($0.lastPathComponent) == key(said) && HomeAccess.allowed($0.path) } }
        if exact.count == 1 { return exact[0] }
        guard exact.isEmpty else { return nil }   // two folders with that name: the model asks which
        var close = first.filter { alike(said, $0.lastPathComponent) }
        if close.isEmpty { close = first.flatMap(folders).filter { alike(said, $0.lastPathComponent) && HomeAccess.allowed($0.path) } }
        return close.count == 1 ? close[0] : nil
    }

    private static func distance(_ a: String, _ b: String) -> Int {
        let a = Array(a), b = Array(b)
        guard abs(a.count - b.count) <= 4 else { return 9 }
        var row = Array(0...b.count)
        for i in 1...a.count {
            var previous = row[0]
            row[0] = i
            for j in 1...b.count {
                let current = row[j]
                row[j] = min(row[j] + 1, row[j - 1] + 1, previous + (a[i - 1] == b[j - 1] ? 0 : 1))
                previous = current
            }
        }
        return row[b.count]
    }
}
