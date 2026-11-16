import AppKit
import PDFKit
import WebKit

/// PDFs made directly (12 November): combining the monthly calendar PDFs with a report took JARVIS
/// 151 clicks in PdfCraft in 17 minutes. PDFKit does it in under a second. Files are only read;
/// the result is always a new file, never one that already exists.
@MainActor
enum PDFTools {
    // MARK: Paths

    static func files(_ value: Any?) -> [URL] {
        let list: [String]
        if let array = value as? [Any] { list = array.compactMap { $0 as? String } }
        else if let text = value as? String { list = text.components(separatedBy: CharacterSet(charactersIn: "\n;")) }
        else { list = [] }
        return list.map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }.filter { !$0.isEmpty }
            .map { URL(fileURLWithPath: ($0 as NSString).expandingTildeInPath) }
    }

    private static func inHome(_ url: URL) -> Bool {
        let path = url.standardizedFileURL.path
        return path.hasPrefix(HomeAccess.home + "/") && HomeAccess.allowed(path)
    }

    /// The new file: in `folder` (default: the first file's folder, else the Desktop), "name (2).pdf" if taken.
    private static func output(_ name: String?, near first: URL?, folder: String?, ext: String = "pdf") -> URL {
        let desktop = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Desktop")
        let dir = folder.map { URL(fileURLWithPath: ($0 as NSString).expandingTildeInPath) } ?? first?.deletingLastPathComponent() ?? desktop
        var base = (name?.trimmingCharacters(in: .whitespaces)).flatMap { $0.isEmpty ? nil : $0 } ?? "Documento combinato"
        base = base.replacingOccurrences(of: "/", with: "-").replacingOccurrences(of: ":", with: ".")
        if base.lowercased().hasSuffix("." + ext) { base = String(base.dropLast(ext.count + 1)) }
        var url = dir.appendingPathComponent(base + "." + ext)
        var n = 2
        while FileManager.default.fileExists(atPath: url.path) {
            url = dir.appendingPathComponent("\(base) (\(n)).\(ext)")
            n += 1
        }
        return url
    }

    private static func check(_ urls: [URL]) -> String? {
        guard !urls.isEmpty else { return "Mancano i file: usa search_files per trovarne il percorso." }
        for url in urls {
            guard inHome(url) else { return "Lavoro solo sui file della tua cartella Inizio: \(url.path)" }
            guard FileManager.default.fileExists(atPath: url.path) else { return "Non trovo \(url.path): cercalo con search_files." }
        }
        return nil
    }

    private static func done(_ url: URL, pages: Int, open: Bool) -> String {
        if open { NSWorkspace.shared.activateFileViewerSelecting([url]) }
        return "Creato \(url.lastPathComponent) (\(pages) \(pages == 1 ? "pagina" : "pagine")) in \(url.deletingLastPathComponent().path)."
    }

    // MARK: Tools

    /// PDFs and images, in the order given, into one PDF.
    static func merge(_ args: [String: Any]) -> String {
        let inputs = files(args["files"])
        if let problem = check(inputs) { return problem }
        let result = PDFDocument()
        var skipped: [String] = []
        for url in inputs {
            if url.pathExtension.lowercased() == "pdf" {
                guard let doc = PDFDocument(url: url) else { skipped.append(url.lastPathComponent); continue }
                if doc.isLocked { skipped.append(url.lastPathComponent + " (protetto da password)"); continue }
                for i in 0..<doc.pageCount { if let page = doc.page(at: i) { result.insert(page, at: result.pageCount) } }
            } else if let image = NSImage(contentsOf: url), let page = PDFPage(image: image) {
                result.insert(page, at: result.pageCount)
            } else {
                skipped.append(url.lastPathComponent)
            }
        }
        guard result.pageCount > 0 else { return "Nessuna pagina da unire: \(skipped.joined(separator: ", ")) non leggibili." }
        let target = output(args.string("name"), near: inputs.first, folder: args.string("folder"))
        guard result.write(to: target) else { return "Non riesco a scrivere \(target.path)." }
        return done(target, pages: result.pageCount, open: args.bool("show") ?? true)
            + (skipped.isEmpty ? "" : " Saltati: \(skipped.joined(separator: ", ")).")
    }

    /// Some pages of a PDF into a new one: "1-3, 5".
    static func extract(_ args: [String: Any]) -> String {
        let inputs = files(args["file"])
        if let problem = check(inputs) { return problem }
        guard let doc = PDFDocument(url: inputs[0]) else { return "\(inputs[0].lastPathComponent) non è un PDF leggibile." }
        let spec = args.string("pages") ?? ""
        var numbers: [Int] = []
        for part in spec.split(separator: ",") {
            let bounds = part.split(separator: "-").compactMap { Int($0.trimmingCharacters(in: .whitespaces)) }
            if bounds.count == 1 { numbers.append(bounds[0]) }
            else if bounds.count == 2, bounds[0] <= bounds[1] { numbers += Array(bounds[0]...bounds[1]) }
        }
        numbers = numbers.filter { (1...doc.pageCount).contains($0) }
        guard !numbers.isEmpty else { return "Pagine non valide: il PDF ne ha \(doc.pageCount). Esempio: \"1-3, 5\"." }
        let result = PDFDocument()
        for n in numbers { if let page = doc.page(at: n - 1) { result.insert(page, at: result.pageCount) } }
        let name = args.string("name") ?? inputs[0].deletingPathExtension().lastPathComponent + " pagine \(spec)"
        let target = output(name, near: inputs[0], folder: args.string("folder"))
        guard result.write(to: target) else { return "Non riesco a scrivere \(target.path)." }
        return done(target, pages: result.pageCount, open: args.bool("show") ?? true)
    }

    static func info(_ args: [String: Any]) -> String {
        let inputs = files(args["file"])
        if let problem = check(inputs) { return problem }
        guard let doc = PDFDocument(url: inputs[0]) else { return "Non è un PDF leggibile." }
        let size = (try? inputs[0].resourceValues(forKeys: [.fileSizeKey]).fileSize).map { ByteCountFormatter.string(fromByteCount: Int64($0), countStyle: .file) } ?? "?"
        let title = (doc.documentAttributes?[PDFDocumentAttribute.titleAttribute] as? String).map { ", titolo \"\($0)\"" } ?? ""
        return "\(inputs[0].lastPathComponent): \(doc.pageCount) pagine, \(size)\(title)\(doc.isLocked ? ", protetto da password" : "")."
    }

    // MARK: Documents written by JARVIS (12 November: a news report typed into TextEdit got lost in the save panel)

    /// A text as an A4 PDF: "# " lines are headings, "- " lines are bullets, the rest paragraphs.
    /// The folder is created if missing; an existing file is never replaced.
    static func fromText(_ args: [String: Any]) -> String {
        guard let text = (args["text"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines), !text.isEmpty else { return "Manca il testo del documento." }
        let title = args.string("title") ?? ""
        let folder = args.string("folder").map(FolderNames.resolve)
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Desktop")
        guard inHome(folder.appendingPathComponent("x")) else { return "Lavoro solo nella tua cartella Inizio." }
        do { try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true) }
        catch { return "Non riesco a creare la cartella \(folder.path): \(error.localizedDescription)" }
        var target = output(args.string("name") ?? (title.isEmpty ? "Documento" : title), near: nil, folder: folder.path)
        // "Aggiungi al report": the document JARVIS made before is updated in place. Only its own documents
        // (marked when created); the old version goes to the Trash, so it can be taken back.
        var replaced = false
        if args.bool("replace") == true {
            let wanted = args.string("name").map { name -> URL in
                let base = name.lowercased().hasSuffix(".pdf") ? String(name.dropLast(4)) : name
                return folder.appendingPathComponent(base.replacingOccurrences(of: "/", with: "-") + ".pdf")
            }
            let candidates = [wanted, args.string("folder") == nil && args.string("name") == nil ? lastCreated : nil, wanted == nil ? lastCreated : nil].compactMap { $0 }
            if let old = candidates.first(where: { FileManager.default.fileExists(atPath: $0.path) && isOwn($0) }) {
                do { try FileManager.default.trashItem(at: old, resultingItemURL: nil) } catch { return "Non riesco ad aggiornare \(old.lastPathComponent): \(error.localizedDescription)" }
                target = old
                replaced = true
            }
        }

        let body = NSMutableAttributedString()
        func add(_ line: String, size: CGFloat, bold: Bool = false, before: CGFloat = 0, after: CGFloat = 6, indent: CGFloat = 0, color: NSColor = .black) {
            let style = NSMutableParagraphStyle()
            style.paragraphSpacingBefore = before
            style.paragraphSpacing = after
            style.lineSpacing = 2
            style.firstLineHeadIndent = indent > 0 ? indent - 10 : 0
            style.headIndent = indent
            body.append(NSAttributedString(string: line + "\n", attributes: [
                .font: bold ? NSFont.boldSystemFont(ofSize: size) : NSFont.systemFont(ofSize: size),
                .foregroundColor: color, .paragraphStyle: style]))
        }
        if !title.isEmpty { add(title, size: 20, bold: true, after: 4) }
        let day = Date().formatted(.dateTime.weekday(.wide).day().month(.wide).year().locale(Locale(identifier: "it_IT")))
        add(day.prefix(1).uppercased() + day.dropFirst(), size: 9.5, after: 14, color: .darkGray)
        for raw in text.components(separatedBy: "\n") {
            let line = raw.trimmingCharacters(in: .whitespaces).replacingOccurrences(of: "**", with: "")
            if line.isEmpty { continue }
            if line.hasPrefix("## ") { add(String(line.dropFirst(3)), size: 12.5, bold: true, before: 8, after: 4) }
            else if line.hasPrefix("# ") { add(String(line.dropFirst(2)), size: 15, bold: true, before: 12, after: 5) }
            else if line.hasPrefix("- ") || line.hasPrefix("• ") { add("•  " + line.dropFirst(2), size: 11, after: 4, indent: 14) }
            else { add(line, size: 11) }
        }

        // A4 with 2 cm margins; Core Text fills one page after the other.
        var page = CGRect(x: 0, y: 0, width: 595, height: 842)
        guard let context = CGContext(target as CFURL, mediaBox: &page, nil) else { return "Non riesco a scrivere \(target.path)." }
        let framesetter = CTFramesetterCreateWithAttributedString(body)
        let area = page.insetBy(dx: 57, dy: 57)
        var start = 0, pages = 0
        while start < body.length, pages < 200 {
            context.beginPDFPage(nil)
            let frame = CTFramesetterCreateFrame(framesetter, CFRange(location: start, length: 0), CGPath(rect: area, transform: nil), nil)
            CTFrameDraw(frame, context)
            context.endPDFPage()
            let visible = CTFrameGetVisibleStringRange(frame).length
            guard visible > 0 else { break }
            start += visible
            pages += 1
        }
        context.closePDF()
        markOwn(target)
        lastCreated = target
        let result = done(target, pages: max(pages, 1), open: args.bool("show") ?? true) + " Percorso: \(target.path)"
        return replaced ? result.replacingOccurrences(of: "Creato ", with: "Aggiornato ") + " La versione di prima è nel Cestino." : result
    }

    /// Documents written by JARVIS carry a mark (an extended attribute): only those may be updated in place.
    private static let ownMark = "com.jarvis.documento"
    private static var lastCreated: URL?

    private static func markOwn(_ url: URL) {
        _ = "1".withCString { setxattr(url.path, ownMark, $0, 1, 0, 0) }
    }

    private static func isOwn(_ url: URL) -> Bool {
        getxattr(url.path, ownMark, nil, 0, 0, 0) > 0
    }

    static func createFolder(_ args: [String: Any]) -> String {
        guard let name = args.string("name"), !name.isEmpty else { return "Manca il nome della cartella." }
        let parent = args.string("in").map(FolderNames.resolve)
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Desktop")
        let folder = name.hasPrefix("/") || name.hasPrefix("~") ? URL(fileURLWithPath: (name as NSString).expandingTildeInPath)
            : parent.appendingPathComponent(name.replacingOccurrences(of: "/", with: "-"))
        guard inHome(folder) else { return "Lavoro solo nella tua cartella Inizio." }
        var isDirectory: ObjCBool = false
        if FileManager.default.fileExists(atPath: folder.path, isDirectory: &isDirectory) {
            return isDirectory.boolValue ? "La cartella \(folder.path) c'è già." : "Esiste già un file con quel nome: \(folder.path)"
        }
        do { try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true) }
        catch { return "Non riuscito: \(error.localizedDescription)" }
        return "Creata la cartella \(folder.path)."
    }

    /// Renames a file in its folder (or moves it to `folder`); never over an existing file.
    static func rename(_ args: [String: Any]) -> String {
        let inputs = files(args["path"])
        if let problem = check(inputs) { return problem }
        let source = inputs[0]
        // Only a folder given: the file moves there with its name.
        let newName = args.string("new_name").flatMap { $0.isEmpty ? nil : $0 } ?? source.lastPathComponent
        guard args.string("new_name")?.isEmpty == false || args.string("folder") != nil else { return "Manca il nuovo nome o la cartella." }
        let ext = source.pathExtension
        let base = newName.lowercased().hasSuffix("." + ext.lowercased()) || ext.isEmpty ? newName : newName + "." + ext
        let dir = args.string("folder").map(FolderNames.resolve) ?? source.deletingLastPathComponent()
        let target = dir.appendingPathComponent(base.replacingOccurrences(of: "/", with: "-"))
        guard inHome(target) else { return "Lavoro solo nella tua cartella Inizio." }
        guard !FileManager.default.fileExists(atPath: target.path) else { return "Esiste già \(target.lastPathComponent): scegli un altro nome." }
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        do { try FileManager.default.moveItem(at: source, to: target) } catch { return "Non riuscito: \(error.localizedDescription)" }
        return "Rinominato in \(target.lastPathComponent)" + (dir != source.deletingLastPathComponent() ? " in \(dir.path)." : ".")
    }

    /// A public web page saved as PDF (no login: pages that need one go through Safari, File › Esporta come PDF).
    static func webpage(_ args: [String: Any]) async -> String {
        guard var link = args.string("url") else { return "Manca l'indirizzo." }
        if !link.contains("://") { link = "https://" + link }
        guard let url = URL(string: link), ["http", "https"].contains(url.scheme ?? "") else { return "Indirizzo non valido." }
        let view = WKWebView(frame: NSRect(x: 0, y: 0, width: 1024, height: 1400))
        let waiter = LoadWaiter()
        view.navigationDelegate = waiter
        view.load(URLRequest(url: url, timeoutInterval: 30))
        guard await waiter.wait(seconds: 30) else { return "La pagina non si è caricata." }
        try? await Task.sleep(for: .seconds(1))   // late images and fonts
        let configuration = WKPDFConfiguration()
        guard let data = try? await view.pdf(configuration: configuration), let doc = PDFDocument(data: data) else {
            return "Non riesco a creare il PDF della pagina."
        }
        let name = args.string("name") ?? (view.title?.isEmpty == false ? view.title! : url.host ?? "Pagina")
        let target = output(name, near: nil, folder: args.string("folder"))
        guard doc.write(to: target) else { return "Non riesco a scrivere \(target.path)." }
        return done(target, pages: doc.pageCount, open: args.bool("show") ?? true)
    }

    private final class LoadWaiter: NSObject, WKNavigationDelegate {
        private var continuation: CheckedContinuation<Bool, Never>?
        private var finished: Bool?

        func wait(seconds: Double) async -> Bool {
            if let finished { return finished }
            return await withCheckedContinuation { c in
                continuation = c
                DispatchQueue.main.asyncAfter(deadline: .now() + seconds) { [weak self] in self?.finish(false) }
            }
        }

        private func finish(_ ok: Bool) {
            guard finished == nil else { return }
            finished = ok
            continuation?.resume(returning: ok)
            continuation = nil
        }

        func webView(_ webView: WKWebView, didFinish navigation: WKNavigation!) { finish(true) }
        func webView(_ webView: WKWebView, didFail navigation: WKNavigation!, withError error: Error) { finish(false) }
        func webView(_ webView: WKWebView, didFailProvisionalNavigation navigation: WKNavigation!, withError error: Error) { finish(false) }
    }
}
