import Foundation
import SQLite3

/// Reads Mail's own index on disk ("Envelope Index"), read-only: unread mail and searches in
/// under a second, even with Mail closed. Mail through AppleScript took 4 to 57 seconds with a
/// 15,000-message Gmail inbox (12 November), so the briefing kept saying "Mail non ha risposto".
///
/// It needs Full Disk Access for JARVIS (System Settings › Privacy & Security). Without it, or if
/// a future macOS changes the tables, every call returns nil and MailActions falls back to AppleScript.
/// The message ROWID is the same id AppleScript gives, so read_email keeps working.
enum MailStore {
    struct Message {
        let id: Int64
        let sender: String
        let subject: String
        let date: Date
        var address = ""
        /// Mail's own mark for mailing lists (the message has a List-Id or an unsubscribe header).
        var isList = false
    }

    /// Gmail keeps every message in "All Mail" and puts it in the inbox with a label,
    /// so a message is in the inbox if its own mailbox or one of its labels is an INBOX.
    private static let inInbox = """
        m.deleted = 0 AND (mb.url LIKE '%/INBOX' OR m.ROWID IN (
            SELECT l.message_id FROM labels l JOIN mailboxes x ON x.ROWID = l.mailbox_id WHERE x.url LIKE '%/INBOX'))
        """

    private static let columns = """
        SELECT m.ROWID, COALESCE(a.comment, ''), COALESCE(a.address, ''), COALESCE(s.subject, ''), m.date_received
        FROM messages m
        JOIN mailboxes mb ON mb.ROWID = m.mailbox
        LEFT JOIN addresses a ON a.ROWID = m.sender
        LEFT JOIN subjects s ON s.ROWID = m.subject
        """

    /// Unread count in the inbox and the most recent unread ones.
    static func unread(limit: Int) -> (total: Int, messages: [Message])? {
        withDatabase { db in
            var total = 0
            guard step(db, "SELECT COUNT(*) FROM messages m JOIN mailboxes mb ON mb.ROWID = m.mailbox WHERE m.read = 0 AND \(inInbox)",
                       row: { total = Int(sqlite3_column_int64($0, 0)) }) else { return nil }
            var list: [Message] = []
            guard step(db, "\(columns) WHERE m.read = 0 AND \(inInbox) ORDER BY m.date_received DESC LIMIT \(max(1, min(limit, 50)))",
                       row: { list.append(message($0)) }) else { return nil }
            return (total, list)
        }
    }

    /// Unread inbox messages of the last days, newest first, with what is needed to sort them by importance.
    static func recentUnread(days: Int, limit: Int = 300) -> [Message]? {
        withDatabase { db in
            // Not every macOS has these columns: without them only the sender tells a list apart.
            var names = Set<String>()
            _ = step(db, "PRAGMA table_info(messages)", row: { names.insert(sqlite3_column_text($0, 1).map { String(cString: $0) } ?? "") })
            let marks = ["list_id_hash", "unsubscribe_type"].filter(names.contains)
            let list = marks.isEmpty ? "0" : marks.map { "COALESCE(m.\($0), 0) != 0" }.joined(separator: " OR ")
            let since = Int(Date().timeIntervalSince1970) - days * 86_400
            var found: [Message] = []
            let sql = columns.replacingOccurrences(of: "m.date_received\n", with: "m.date_received, (\(list))\n")
                + " WHERE m.read = 0 AND m.date_received >= \(since) AND \(inInbox) ORDER BY m.date_received DESC LIMIT \(limit)"
            guard step(db, sql, row: {
                var m = message($0)
                m.isList = sqlite3_column_int($0, 5) != 0
                found.append(m)
            }) else { return nil }
            return found
        }
    }

    /// Addresses the user has written to, and his own: taken from the sent mail. Empty if the tables differ.
    static func correspondents() -> (own: Set<String>, writtenTo: Set<String>) {
        let sent = """
            SELECT m.ROWID FROM messages m JOIN mailboxes mb ON mb.ROWID = m.mailbox WHERE mb.url LIKE '%Sent%' OR mb.url LIKE '%inviat%'
            UNION SELECT l.message_id FROM labels l JOIN mailboxes x ON x.ROWID = l.mailbox_id WHERE x.url LIKE '%Sent%' OR x.url LIKE '%inviat%'
            """
        return withDatabase { db -> (own: Set<String>, writtenTo: Set<String>)? in
            var own = Set<String>(), to = Set<String>()
            _ = step(db, "SELECT DISTINCT lower(a.address) FROM messages m JOIN addresses a ON a.ROWID = m.sender WHERE m.ROWID IN (\(sent)) LIMIT 50",
                     row: { own.insert(sqlite3_column_text($0, 0).map { String(cString: $0) } ?? "") })
            _ = step(db, "SELECT DISTINCT lower(a.address) FROM recipients r JOIN addresses a ON a.ROWID = r.address WHERE r.message IN (\(sent)) LIMIT 5000",
                     row: { to.insert(sqlite3_column_text($0, 0).map { String(cString: $0) } ?? "") })
            return (own, to.subtracting(own))
        } ?? ([], [])
    }

    /// Inbox messages whose subject or sender contains the text, most recent first.
    static func search(_ query: String, limit: Int = 8) -> [Message]? {
        withDatabase { db in
            var list: [Message] = []
            let sql = "\(columns) WHERE \(inInbox) AND (s.subject LIKE ?1 OR a.address LIKE ?1 OR a.comment LIKE ?1) "
                + "ORDER BY m.date_received DESC LIMIT \(limit)"
            guard step(db, sql, text: "%\(query)%", row: { list.append(message($0)) }) else { return nil }
            return list
        }
    }

    // MARK: SQLite

    /// The newest "V…" folder: macOS moves it on upgrades (V10 on macOS 14–26).
    private static var path: String? {
        let root = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Mail")
        guard let folders = try? FileManager.default.contentsOfDirectory(atPath: root.path) else { return nil }
        let newest = folders.filter { $0.hasPrefix("V") }.compactMap { name in Int(name.dropFirst()).map { ($0, name) } }.max { $0.0 < $1.0 }
        guard let newest else { return nil }
        let file = root.appendingPathComponent("\(newest.1)/MailData/Envelope Index").path
        return FileManager.default.fileExists(atPath: file) ? file : nil
    }

    private static var lastProblem = ""

    private static func withDatabase<T>(_ body: (OpaquePointer) -> T?) -> T? {
        guard let path else { return report("archivio di Mail non leggibile (manca l'accesso completo al disco?)") }
        var db: OpaquePointer?
        let uri = "file:" + (path.addingPercentEncoding(withAllowedCharacters: .urlPathAllowed) ?? path) + "?mode=ro"
        guard sqlite3_open_v2(uri, &db, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI, nil) == SQLITE_OK, let db else {
            sqlite3_close(db)
            return report("apertura non riuscita")
        }
        defer { sqlite3_close(db) }
        sqlite3_busy_timeout(db, 2000)
        let result = body(db)
        if result == nil { return report(String(cString: sqlite3_errmsg(db))) }
        return result
    }

    /// Logged once per kind of problem, so diagnostica.log shows why the slow way was used.
    private static func report<T>(_ problem: String) -> T? {
        if problem != lastProblem {
            lastProblem = problem
            Diagnostics.log("mail      archivio: \(problem); uso AppleScript")
        }
        return nil
    }

    private static func step(_ db: OpaquePointer, _ sql: String, text: String? = nil, row: (OpaquePointer) -> Void) -> Bool {
        var statement: OpaquePointer?
        guard sqlite3_prepare_v2(db, sql, -1, &statement, nil) == SQLITE_OK, let statement else { return false }
        defer { sqlite3_finalize(statement) }
        if let text { sqlite3_bind_text(statement, 1, text, -1, unsafeBitCast(-1, to: sqlite3_destructor_type.self)) }
        while true {
            switch sqlite3_step(statement) {
            case SQLITE_ROW: row(statement)
            case SQLITE_DONE: lastProblem = ""; return true
            default: return false
            }
        }
    }

    private static func message(_ s: OpaquePointer) -> Message {
        func text(_ i: Int32) -> String { sqlite3_column_text(s, i).map { String(cString: $0) } ?? "" }
        let name = text(1).trimmingCharacters(in: CharacterSet(charactersIn: "\" "))
        return Message(id: sqlite3_column_int64(s, 0), sender: name.isEmpty ? text(2) : name, subject: text(3),
                       date: Date(timeIntervalSince1970: Double(sqlite3_column_int64(s, 4))), address: text(2).lowercased())
    }
}
