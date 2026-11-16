import Contacts
import Foundation

/// Mail through the Mail app (all the accounts configured there).
/// Reading is free; sending always goes through a spoken confirmation (see Assistant).
enum MailActions {
    private static let field = "‖"   // separator unlikely in email texts

    static func listUnread(_ args: [String: Any]) async -> String {
        let limit = Int(args.number("limit") ?? 10)
        // Mail's index on disk answers at once; AppleScript only if it cannot be read.
        if let found = MailStore.unread(limit: limit) {
            if found.total == 0 { return "Nessuna email non letta." }
            if args.bool("all") != true, let sorted = await byImportance(total: found.total, limit: limit) { return sorted }
            return "Non lette: \(found.total). Le più recenti:\n" + found.messages.map(line).joined(separator: "\n")
        }
        let script = """
        tell application "Mail"
            -- Con una posta grande "whose read status" non finisce mai: il conto è immediato
            -- e i non letti si cercano solo tra i messaggi più recenti, che stanno in cima.
            set total to unread count of inbox
            set out to (total as string) & linefeed
            if total > 0 then
                set shown to 0
                set recent to count of messages of inbox
                if recent > 150 then set recent to 150
                repeat with i from 1 to recent
                    set m to message i of inbox
                    if read status of m is false then
                        set out to out & (id of m) & "\(field)" & (sender of m) & "\(field)" & (subject of m) & "\(field)" & ((date received of m) as string) & linefeed
                        set shown to shown + 1
                        if shown is \(limit) then exit repeat
                    end if
                end repeat
            end if
            return out
        end tell
        """
        let result = await Shell.osascript(script, timeout: args.number("timeout") ?? 25)
        guard result.ok else { return result.err.isEmpty ? "Mail non ha risposto in tempo." : "Errore Mail: \(result.err)" }
        var lines = result.out.split(separator: "\n").map(String.init)
        guard let total = lines.first else { return "Nessuna risposta da Mail" }
        lines.removeFirst()
        if total == "0" { return "Nessuna email non letta." }
        let items = lines.map { line -> String in
            let p = line.components(separatedBy: field)
            guard p.count >= 4 else { return line }
            return "- [id \(p[0])] da \(cleanSender(p[1])): \"\(p[2])\" (\(p[3]))"
        }
        return "Non lette: \(total). Le più recenti:\n" + items.joined(separator: "\n")
    }

    static func search(_ args: [String: Any]) async -> String {
        guard let query = args.string("query") else { return "Ricerca mancante" }
        if let found = MailStore.search(query) {
            return found.isEmpty ? "Nessuna email trovata per \(query)" : found.map(line).joined(separator: "\n")
        }
        let q = Shell.quote(query)
        let script = """
        tell application "Mail"
            set msgs to (messages of inbox whose subject contains \(q) or sender contains \(q))
            set n to count of msgs
            if n > 8 then set n to 8
            set out to ""
            repeat with i from 1 to n
                set m to item i of msgs
                set out to out & (id of m) & "\(field)" & (sender of m) & "\(field)" & (subject of m) & "\(field)" & ((date received of m) as string) & linefeed
            end repeat
            return out
        end tell
        """
        let result = await Shell.osascript(script, timeout: 60)
        guard result.ok else { return "Errore Mail: \(result.err)" }
        guard !result.out.isEmpty else { return "Nessuna email trovata per \(query)" }
        return result.out.split(separator: "\n").map { line in
            let p = String(line).components(separatedBy: field)
            guard p.count >= 4 else { return String(line) }
            return "- [id \(p[0])] da \(cleanSender(p[1])): \"\(p[2])\" (\(p[3]))"
        }.joined(separator: "\n")
    }

    static func read(_ args: [String: Any]) async -> String {
        guard let id = args.number("id").map({ Int($0) }) else { return "Serve l'id della mail (da list_unread_emails o search_emails)" }
        let script = """
        tell application "Mail"
            set m to first message of inbox whose id is \(id)
            return (sender of m) & linefeed & (subject of m) & linefeed & ((date received of m) as string) & linefeed & linefeed & (content of m)
        end tell
        """
        let result = await Shell.osascript(script, timeout: 30)
        guard result.ok else { return "Errore leggendo la mail: \(result.err)" }
        let text = result.out.replacingOccurrences(of: #"\n{3,}"#, with: "\n\n", options: .regularExpression)
        return String(text.prefix(4000))
    }

    /// Actually sends: called only after the spoken confirmation.
    static func send(to: String, subject: String, body: String) async -> String {
        let script = """
        tell application "Mail"
            set m to make new outgoing message with properties {subject:\(Shell.quote(subject)), content:\(Shell.quote(body)), visible:false}
            tell m to make new to recipient at end of to recipients with properties {address:\(Shell.quote(to))}
            send m
        end tell
        """
        let result = await Shell.osascript(script, timeout: 45)
        return result.ok ? "Email inviata a \(to)" : "Errore inviando: \(result.err)"
    }

    /// "Marco" → email address from Contacts; an address stays as it is.
    static func resolveRecipient(_ value: String) async -> (address: String?, label: String) {
        if value.contains("@") { return (value, value) }
        let store = CNContactStore()
        guard (try? await store.requestAccess(for: .contacts)) == true else { return (nil, value) }
        let keys = [CNContactGivenNameKey, CNContactFamilyNameKey, CNContactEmailAddressesKey] as [CNKeyDescriptor]
        let contacts = (try? store.unifiedContacts(matching: CNContact.predicateForContacts(matchingName: value), keysToFetch: keys)) ?? []
        guard let contact = contacts.first(where: { !$0.emailAddresses.isEmpty }),
              let email = contact.emailAddresses.first?.value as String? else { return (nil, value) }
        return (email, "\(contact.givenName) \(contact.familyName) <\(email)>".trimmingCharacters(in: .whitespaces))
    }

    // MARK: Importance (12 November: 14,507 unread, nearly all advertising; the latest ten were newsletters)

    enum Rank { case important(String), notice, other, advertising }

    /// Senders set by voice ("le mail di X sono importanti", "ignora le mail di Y"): parts of a name or address.
    nonisolated(unsafe) static var importantSenders: [String] = []
    nonisolated(unsafe) static var ignoredSenders: [String] = []

    private static let publicProviders: Set<String> = ["gmail.com", "googlemail.com", "icloud.com", "me.com", "mac.com", "outlook.com", "outlook.it",
                                                       "hotmail.com", "hotmail.it", "live.com", "live.it", "yahoo.com", "yahoo.it", "libero.it",
                                                       "virgilio.it", "tiscali.it", "alice.it", "tim.it", "fastwebnet.it", "proton.me", "protonmail.com"]
    private static let automatic = #"(^|[._-])(no-?_?reply|do-?not-?reply|newsletter|news|promo\w*|marketing|offerte|offers|deals|mailer|campaign\w*|notification\w*|notifiche|info|hello|team|update\w*|digest|comunicazion\w*|store|shop|sales|ecommerce|community|feedback|survey|rewards|member\w*)([._+-]|@)"#
    private static let bulkDomain = #"@(e|em|email|emails|mail|mailing|news|newsletter\w*|info|marketing|promo|engage|updates?|comunicazioni|notifiche|t|sg|click|go|m|mkt|campaign\w*|reply)\."#
    private static let noticeWords = #"\b(sicurezza|security|accesso|password|verifica|codice|fattura|bolletta|pagamento|ricevuta|ordine|spedizione|consegna|scadenza|rinnovo|addebito|bonifico|invoice|receipt|payment|order|shipped|delivery|appuntamento|prenotazione|conferma)\b"#

    private static var contactsCache: (at: Date, emails: Set<String>)?

    private static func contactEmails() async -> Set<String> {
        if let cache = contactsCache, Date().timeIntervalSince(cache.at) < 900 { return cache.emails }
        let store = CNContactStore()
        guard (try? await store.requestAccess(for: .contacts)) == true else { return [] }
        var emails = Set<String>()
        let request = CNContactFetchRequest(keysToFetch: [CNContactEmailAddressesKey as CNKeyDescriptor])
        try? store.enumerateContacts(with: request) { contact, _ in
            for email in contact.emailAddresses { emails.insert((email.value as String).lowercased()) }
        }
        contactsCache = (Date(), emails)
        return emails
    }

    static func rank(_ m: MailStore.Message, contacts: Set<String>, own: Set<String>, writtenTo: Set<String>) -> Rank {
        let address = m.address, who = (m.sender + " " + address).lowercased()
        let domain = address.split(separator: "@").last.map(String.init) ?? ""
        if ignoredSenders.contains(where: { who.contains($0.lowercased()) }) { return .advertising }
        if importantSenders.contains(where: { who.contains($0.lowercased()) }) { return .important("scelto da te") }
        if contacts.contains(address) { return .important("nei contatti") }
        let looksAutomatic = m.isList || address.range(of: automatic, options: .regularExpression) != nil
            || address.range(of: bulkDomain, options: .regularExpression) != nil
        // A reply once sent to a shop or a marketplace relay does not make its mailings important.
        if writtenTo.contains(address), !looksAutomatic { return .important("gli hai scritto") }
        // The same organisation as one of his addresses (university, company), written by a person.
        let ownDomains = Set(own.compactMap { $0.split(separator: "@").last.map(String.init) }).subtracting(publicProviders)
        let sameOrganisation = ownDomains.contains { domain == $0 || domain.hasSuffix("." + $0) || $0.hasSuffix("." + domain) }
        // Its mass mailings (notices to all students) are not advertising, but not urgent either.
        if sameOrganisation { return looksAutomatic ? .other : .important("della tua organizzazione") }
        if m.subject.lowercased().range(of: noticeWords, options: .regularExpression) != nil { return .notice }
        return looksAutomatic ? .advertising : .other
    }

    /// The unread mail of the last week sorted: important first, then account and payment notices, then the rest.
    private static func byImportance(total: Int, limit: Int) async -> String? {
        guard let recent = MailStore.recentUnread(days: 7), !recent.isEmpty else { return nil }
        let contacts = await contactEmails()
        let (own, writtenTo) = MailStore.correspondents()
        var important: [String] = [], notices: [String] = [], others: [String] = []
        var advertising = 0
        for m in recent {
            switch rank(m, contacts: contacts, own: own, writtenTo: writtenTo) {
            case .important(let why):
                important.append(line(m) + " [\(why)]")
                Diagnostics.log("mail      importante: \(m.sender) <\(m.address)> (\(why)\(m.isList ? ", lista" : ""))")
            case .notice: notices.append(line(m))
            case .other: others.append(line(m))
            case .advertising: advertising += 1
            }
        }
        Diagnostics.log("mail      ultimi 7 giorni: \(important.count) importanti, \(notices.count) avvisi, \(others.count) altre, \(advertising) pubblicità; contatti \(contacts.count), scritti \(writtenTo.count), tuoi indirizzi \(own.count)")
        var out = "Non lette in tutto: \(total), quasi tutte vecchie liste e pubblicità. Negli ultimi 7 giorni: \(important.count) importanti, \(notices.count) avvisi (account, pagamenti, ordini), \(others.count) altre, \(advertising) pubblicità o liste."
        out += "\nIMPORTANTI:\n" + (important.isEmpty ? "nessuna" : important.prefix(limit).joined(separator: "\n"))
        if !notices.isEmpty { out += "\nAVVISI:\n" + notices.prefix(5).joined(separator: "\n") }
        if !others.isEmpty { out += "\nALTRE:\n" + others.prefix(5).joined(separator: "\n") }
        return out + "\nSay the important ones (who and what, briefly), then at most the notices that matter in a few words; never list the advertising, only its number if asked. For every unread mail regardless of importance, call again with all=true."
    }

    private static func line(_ m: MailStore.Message) -> String {
        "- [id \(m.id)] da \(m.sender): \"\(m.subject)\" (\(DateArg.spoken(m.date)))"
    }

    private static func cleanSender(_ sender: String) -> String {
        // "Mario Rossi <mario@x.it>" → "Mario Rossi"
        if let range = sender.range(of: " <") { return String(sender[..<range.lowerBound]).replacingOccurrences(of: "\"", with: "") }
        return sender
    }
}
