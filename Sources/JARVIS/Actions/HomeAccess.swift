import Foundation

/// Which files in the home JARVIS may find, open, move and write (13 November, user request:
/// "dagli accesso a Libreria, e anche ad altre cartelle che non sono proprie del sistema").
///
/// Everything in the home, except:
/// - hidden folders (.ssh, .config…);
/// - ~/Library, apart from the app data the user works with: Application Support (Minecraft mods,
///   Bambu Studio, VS Code…), iCloud Drive, Google Drive / Dropbox / OneDrive, Arduino15, Fonts;
/// - inside those, macOS's own folders (com.apple…, AddressBook, MobileSync with the iPhone backups…),
///   browser profiles and password managers (passwords and cookies), JARVIS's own data and the AI apps'
///   (access keys), and caches;
/// - files that hold logins anywhere (Cookies, Login Data, logins.json, tokens…).
enum HomeAccess {
    static let home = FileManager.default.homeDirectoryForCurrentUser.standardizedFileURL.path

    /// The ~/Library folders that hold the user's app data, not the system's.
    private static let libraryRoots: Set<String> = ["Application Support", "Mobile Documents", "CloudStorage", "Arduino15", "Fonts"]

    /// Folders of Application Support that belong to macOS or hold passwords, keys or private data.
    private static let privateApps: Set<String> = [
        "addressbook", "animoji", "apple", "askpermission", "callhistorydb", "callhistorytransactions", "clouddocs", "controlcenter",
        "crashreporter", "differentialprivacy", "diskimages", "eventstracker", "facetime", "fileprovider", "intelligenceflow",
        "knowledge", "mobilesync", "spotlight", "appplaceholdersyncd", "appsubscriptions", "studentd", "homeenergyd",
        "jarvis", "claude", "codex", "openai", "ollama",
        "google", "firefox", "bravesoftware", "microsoft edge", "arc", "chromium", "vivaldi", "com.operasoftware.opera", "opera software",
        "1password", "bitwarden", "keepassxc", "dashlane", "signal", "telegram desktop", "whatsapp",
    ]

    /// Names that, anywhere in the path, mean logins, keys or caches.
    private static let privateNames: Set<String> = [
        "cookies", "cookies.sqlite", "login data", "web data", "logins.json", "key4.db", "key3.db", "local storage", "session storage",
        "indexeddb", "keychains", "cache", "caches", "cacheddata", "gpucache", "code cache", "crashpad", "blob_storage", "service worker",
        "node_modules",
    ]

    static func allowed(_ raw: String) -> Bool {
        let path = URL(fileURLWithPath: (raw as NSString).expandingTildeInPath).standardizedFileURL.resolvingSymlinksInPath().path
        // Outside the home only external and network drives.
        guard path.hasPrefix(home + "/") else { return path.hasPrefix("/Volumes/") }
        let parts = path.dropFirst(home.count + 1).split(separator: "/").map(String.init)
        guard let first = parts.first else { return true }
        if parts.contains(where: { $0.hasPrefix(".") }) { return false }
        let lowered = parts.map { $0.lowercased() }
        // "Login Data-journal", "Login Data For Account": the logins of browsers built into apps (Minecraft, Steam,
        // GeForce NOW), and every cache folder ("webcache2", "CefCache", "htmlcache") that holds them.
        if lowered.contains(where: { privateNames.contains($0) || $0.hasPrefix("ssfn") || $0.contains("token") || $0.contains("credential")
            || $0.contains("password") || $0.hasSuffix(".keychain") || $0.hasSuffix(".keychain-db") || $0.contains("cache")
            || $0.hasPrefix("login data") || $0.hasPrefix("web data") || $0.hasPrefix("cookies") || $0 == "history" || $0.hasPrefix("history-") }) { return false }
        guard first == "Library" else { return true }
        guard parts.count >= 2, libraryRoots.contains(parts[1]) else { return false }
        if parts[1] == "Application Support", parts.count >= 3 {
            let app = parts[2].lowercased()
            if app.hasPrefix("com.apple.") || privateApps.contains(app) { return false }
        }
        return true
    }

    /// In ~/Library: found results go after the ones in the usual folders when equally similar.
    static func inLibrary(_ path: String) -> Bool { path.hasPrefix(home + "/Library/") }
}
