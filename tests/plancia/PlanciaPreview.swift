// Standalone preview of the "Plancia" (23 November, option C): one window in the Settings style
// that replaces the seven panels, plus the one quick menu shared by the menu-bar icon, the sphere,
// the satellite and the robot. Invented data only; nothing from the app runs here.
// Build and open with tests/plancia/preview.sh.
import AppKit
import SwiftUI

// MARK: Look (same values as HUD in Reactor.swift)

enum Look {
    static let cyan = Color(red: 0.31, green: 0.86, blue: 1.0)
    static let ice = Color(red: 0.80, green: 0.97, blue: 1.0)
    static let amber = Color(red: 1.0, green: 0.62, blue: 0.20)
    static let green = Color(red: 0.35, green: 0.85, blue: 0.55)
    static let background = Color(red: 0.01, green: 0.04, blue: 0.08)
    static func din(_ size: CGFloat, _ weight: Font.Weight = .regular) -> Font { .custom("DIN Alternate", size: size).weight(weight) }
    static func mono(_ size: CGFloat) -> Font { .system(size: size, design: .monospaced) }
}

// MARK: Sections

enum Area: String, CaseIterable, Identifiable {
    case dialogue, day, status, thought
    var id: Self { self }
    var title: String {
        switch self {
        case .dialogue: return "Dialogo"
        case .day: return "Giornata"
        case .status: return "Stato"
        case .thought: return "Pensiero"
        }
    }
    var icon: String {
        switch self {
        case .dialogue: return "text.bubble"
        case .day: return "calendar.badge.clock"
        case .status: return "waveform.path.ecg"
        case .thought: return "terminal"
        }
    }
    var intro: String {
        switch self {
        case .dialogue: return "Cosa ci siamo detti. Puoi anche scrivermi da qui."
        case .day: return "Le sessioni di studio e lavoro, i resoconti e le schede da ripassare."
        case .status: return "Come stanno le mie parti, e cosa è successo di recente."
        case .thought: return "Cosa faccio mentre rispondo: strade scelte, strumenti, tempi."
        }
    }
}

final class Model: ObservableObject {
    @Published var section: Area = (CommandLine.arguments.firstIndex(of: "--area").flatMap { i in i + 1 < CommandLine.arguments.count ? Area(rawValue: CommandLine.arguments[i + 1]) : nil } ?? .dialogue)
    @Published var brain = "Claude"
    @Published var language = "IT"
    @Published var detached: Set<Area> = []
}

// MARK: Plancia window

struct Plancia: View {
    @ObservedObject var model: Model
    var detach: (Area) -> Void

    var body: some View {
        HStack(spacing: 0) {
            sidebar
            Divider().opacity(0.4)
            VStack(spacing: 0) {
                header
                SectionBody(section: model.section)
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        }
        .frame(minWidth: 720, minHeight: 520)
        .background(Look.background.ignoresSafeArea())
        .tint(Look.cyan)
        .preferredColorScheme(.dark)
    }

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 2) {
            NucleusBadge()
                .padding(.horizontal, 8).padding(.bottom, 12)
            ForEach(Area.allCases) { item in
                Button { model.section = item } label: {
                    HStack {
                        Label(item.title, systemImage: item.icon)
                        Spacer()
                        if model.detached.contains(item) {
                            Image(systemName: "rectangle.on.rectangle").font(.caption2).foregroundStyle(.secondary)
                        }
                    }
                    .font(.system(size: 13, weight: item == model.section ? .semibold : .regular))
                    .foregroundStyle(item == model.section ? Look.cyan : .primary)
                    .padding(.horizontal, 10).padding(.vertical, 7)
                    .background(item == model.section ? Look.cyan.opacity(0.12) : .clear, in: RoundedRectangle(cornerRadius: 8))
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
            Spacer()
            Divider().opacity(0.3).padding(.vertical, 6)
            Button {} label: {
                Label("Impostazioni…", systemImage: "gearshape")
                    .font(.system(size: 13)).foregroundStyle(.secondary)
                    .padding(.horizontal, 10).padding(.vertical, 7)
                    .frame(maxWidth: .infinity, alignment: .leading)
            }
            .buttonStyle(.plain)
        }
        .padding(.horizontal, 10).padding(.top, 34).padding(.bottom, 12)
        .frame(width: 200)
        .background(Color.white.opacity(0.03))
    }

    private var header: some View {
        HStack(alignment: .top) {
            VStack(alignment: .leading, spacing: 6) {
                Text(model.section.title).font(.system(size: 22, weight: .semibold))
                Text(model.section.intro).font(.callout).foregroundStyle(.secondary)
            }
            Spacer()
            VStack(alignment: .trailing, spacing: 8) {
                HStack(spacing: 6) {
                    ForEach(["Locale", "Claude", "GPT", "Gemini"], id: \.self) { name in
                        Chip(text: name, on: model.brain == name) { model.brain = name }
                    }
                    Divider().frame(height: 16)
                    ForEach(["IT", "EN"], id: \.self) { name in
                        Chip(text: name, on: model.language == name) { model.language = name }
                    }
                }
                Button { detach(model.section) } label: {
                    Label("Stacca in finestrella", systemImage: "arrow.up.forward.square")
                        .font(.caption).foregroundStyle(Look.cyan.opacity(0.85))
                }
                .buttonStyle(.plain)
                .help("Apre questa sezione in una finestra piccola, da tenere accanto al robottino")
            }
        }
        .padding(.horizontal, 24).padding(.top, 34).padding(.bottom, 10)
    }
}

/// AL-76's state at the top of the sidebar: the link between the window and the character.
struct NucleusBadge: View {
    @State private var pulse = false
    var body: some View {
        HStack(spacing: 10) {
            ZStack {
                Circle().stroke(Look.cyan.opacity(0.35), lineWidth: 1).frame(width: 28, height: 28)
                    .scaleEffect(pulse ? 1.15 : 0.95).opacity(pulse ? 0.2 : 0.8)
                Circle().fill(Look.cyan).frame(width: 10, height: 10).shadow(color: Look.cyan, radius: 5)
            }
            .frame(width: 30, height: 30)
            VStack(alignment: .leading, spacing: 1) {
                Text("AL-76").font(Look.din(15, .bold)).tracking(2).foregroundStyle(Look.ice)
                Text("In ascolto · Claude").font(.caption).foregroundStyle(.secondary)
            }
        }
        .onAppear { withAnimation(.easeInOut(duration: 1.6).repeatForever(autoreverses: true)) { pulse = true } }
    }
}

struct Chip: View {
    let text: String
    let on: Bool
    let action: () -> Void
    var body: some View {
        Button(action: action) {
            Text(text).font(Look.din(11, .bold)).tracking(1).lineLimit(1).fixedSize()
                .padding(.horizontal, 9).padding(.vertical, 4)
                .foregroundStyle(on ? Look.background : Look.cyan)
                .background(on ? Look.cyan : Look.cyan.opacity(0.07), in: Capsule())
                .overlay(Capsule().stroke(Look.cyan.opacity(on ? 0 : 0.4), lineWidth: 1))
        }
        .buttonStyle(.plain)
    }
}

// MARK: Area contents (shared by the Plancia and the detached windows)

struct SectionBody: View {
    let section: Area
    var compact = false

    var body: some View {
        switch section {
        case .dialogue: DialogueSection(compact: compact)
        case .day: DaySection(compact: compact)
        case .status: StatusSection()
        case .thought: ThoughtSection()
        }
    }
}

struct DialogueSection: View {
    var compact: Bool
    @State private var draft = ""
    private let lines: [(Bool, String)] = [
        (true, "Che ore sono a Tokyo?"),
        (false, "Sono le 23:40, otto ore avanti rispetto a noi."),
        (true, "Avvia una sessione di studio di fisica"),
        (false, "Fatto: sessione «Fisica» avviata alle 15:02. Ti avviso tra 50 minuti per la pausa."),
        (true, "Com'è messa la stampante?"),
        (false, "Sta stampando il supporto del braccio: 64%, mancano circa 38 minuti."),
    ]

    var body: some View {
        VStack(spacing: 0) {
            ScrollView {
                VStack(alignment: .leading, spacing: 12) {
                    Text("OGGI · 15:00").font(Look.din(10, .bold)).tracking(2).foregroundStyle(.secondary)
                        .frame(maxWidth: .infinity)
                    ForEach(Array(lines.enumerated()), id: \.offset) { _, line in
                        row(user: line.0, text: line.1)
                    }
                }
                .padding(.horizontal, compact ? 14 : 24).padding(.vertical, 8)
            }
            HStack(spacing: 8) {
                TextField("Scrivi ad AL-76…", text: $draft).textFieldStyle(.plain)
                    .padding(.horizontal, 12).padding(.vertical, 8)
                    .background(Look.cyan.opacity(0.06), in: RoundedRectangle(cornerRadius: 10))
                    .overlay(RoundedRectangle(cornerRadius: 10).stroke(Look.cyan.opacity(0.3), lineWidth: 1))
                Button {} label: { Image(systemName: "mic.fill") }.help("Parla (⌃⌥Spazio)")
                if !compact {
                    Button("Nuova conversazione") {}
                }
            }
            .padding(compact ? 10 : 16)
            .background(Color.white.opacity(0.02))
        }
    }

    private func row(user: Bool, text: String) -> some View {
        HStack(alignment: .top, spacing: 10) {
            Text(user ? "Tu" : "AL-76")
                .font(Look.din(11, .bold)).tracking(1)
                .foregroundStyle(user ? Color.secondary : Look.cyan)
                .frame(width: 44, alignment: .leading)
                .padding(.top, 1)
            Text(text).font(.system(size: 13)).foregroundStyle(user ? Color.primary.opacity(0.85) : Look.ice)
                .textSelection(.enabled)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}

struct DaySection: View {
    var compact: Bool
    @State private var tab = 0

    var body: some View {
        Form {
            Section {
                HStack(spacing: 14) {
                    VStack(alignment: .leading, spacing: 3) {
                        Text("In corso").font(.caption).foregroundStyle(.secondary)
                        HStack(spacing: 6) {
                            Circle().fill(Look.green).frame(width: 7, height: 7)
                            Text("Fisica · studio").font(.system(size: 14, weight: .semibold))
                        }
                    }
                    Spacer()
                    TimelineView(.periodic(from: .now, by: 1)) { ctx in
                        let s = 1712 + Int(ctx.date.timeIntervalSince1970) % 60
                        Text(String(format: "%d:%02d:%02d", s / 3600, s / 60 % 60, s % 60))
                            .font(Look.din(22, .bold)).monospacedDigit().foregroundStyle(Look.ice)
                    }
                }
                HStack {
                    total("Oggi", "2 h 14 min")
                    Spacer()
                    total("Settimana", "11 h 40 min")
                    Spacer()
                    total("Da ripassare", "7 schede")
                }
            }
            Section {
                Picker("", selection: $tab) {
                    Text("Sessioni").tag(0)
                    Text("Resoconti").tag(1)
                    Text("Schede").tag(2)
                }
                .pickerStyle(.segmented).labelsHidden()
                switch tab {
                case 0:
                    item("Fisica", "Studio · 15:02–ora")
                    item("Braccio robotico, firmware", "Lavoro · 10:15–12:40 · 1 pausa")
                    item("Analisi", "Studio · ieri 17:00–18:30")
                case 1:
                    item("Fisica, 22 novembre", "Moto rotazionale, momento d'inerzia: chiaro. Da rivedere: giroscopio.")
                    item("Analisi, 21 novembre", "Serie di Taylor, 12 esercizi, 2 sbagliati.")
                default:
                    item("Cos'è il momento d'inerzia?", "Fisica · da ripassare oggi")
                    item("Raggio di convergenza", "Analisi · tra 3 giorni")
                }
            }
        }
        .formStyle(.grouped)
        .scrollContentBackground(.hidden)
    }

    private func total(_ label: String, _ value: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(label).font(.caption).foregroundStyle(.secondary)
            Text(value).font(Look.din(14, .bold)).foregroundStyle(Look.ice)
        }
    }

    private func item(_ title: String, _ detail: String) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(title).font(.system(size: 13, weight: .medium))
            Text(detail).font(.caption).foregroundStyle(.secondary)
        }
    }
}

struct StatusSection: View {
    private let modules: [(String, String, String, Bool)] = [
        ("waveform", "Chiamata «Ehi Al»", "Attiva · impronta vocale ok", true),
        ("ear", "Udito", "Whisper small · pronto", true),
        ("brain", "Cervello", "Claude Opus · Gemma pronta offline", true),
        ("speaker.wave.2", "Voce", "Qwen3 0.6B · robottino", true),
        ("printer", "Stampante", "Stampa al 64% · 38 min", true),
        ("iphone", "iPhone", "Non collegato", false),
    ]
    private let log = [
        "15:02  sessione «Fisica» avviata",
        "14:58  risposta da Claude in 3,4 s",
        "14:31  stampante: strato 212 di 330",
        "13:10  iPhone scollegato",
        "12:40  sessione «Braccio robotico» chiusa",
    ]

    var body: some View {
        Form {
            Section("Moduli") {
                ForEach(modules, id: \.1) { m in
                    HStack(spacing: 10) {
                        Image(systemName: m.0).frame(width: 20).foregroundStyle(Look.cyan)
                        Text(m.1)
                        Spacer()
                        Text(m.2).font(.callout).foregroundStyle(.secondary)
                        Circle().fill(m.3 ? Look.green : Look.amber).frame(width: 7, height: 7)
                    }
                }
            }
            Section("Registro") {
                ForEach(log, id: \.self) { line in
                    Text(line).font(Look.mono(11)).foregroundStyle(Look.cyan.opacity(0.85)).textSelection(.enabled)
                }
            }
        }
        .formStyle(.grouped)
        .scrollContentBackground(.hidden)
    }
}

struct ThoughtSection: View {
    private let lines: [(String, Color)] = [
        ("15:02:11 » «avvia una sessione di studio di fisica»", Look.ice),
        ("15:02:11 comando istantaneo: activity.start", Look.cyan.opacity(0.9)),
        ("15:02:11 ✓ sessione aperta in 12 ms, nessun modello", .green.opacity(0.8)),
        ("14:58:02 » «com'è messa la stampante?»", Look.ice),
        ("14:58:02 strada: Claude (domanda con strumenti)", Look.cyan.opacity(0.9)),
        ("14:58:03 ← strumento printer_status", Look.cyan.opacity(0.55)),
        ("14:58:05 ✓ risposta in 3,4 s, 212 token", .green.opacity(0.8)),
        ("14:31:40 ✕ meteo: rete lenta, riprovo", Look.amber),
    ]

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 4) {
                ForEach(Array(lines.enumerated()), id: \.offset) { _, l in
                    Text(l.0).font(Look.mono(11)).foregroundStyle(l.1).textSelection(.enabled)
                }
                Text("▌").font(Look.mono(11)).foregroundStyle(Look.ice)
            }
            .frame(maxWidth: .infinity, alignment: .leading)
            .padding(12)
        }
        .background(Color.black.opacity(0.45), in: RoundedRectangle(cornerRadius: 10))
        .padding(.horizontal, 24).padding(.bottom, 20)
    }
}

// MARK: Detached small window

struct Detached: View {
    let section: Area
    var back: () -> Void
    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Label(section.title, systemImage: section.icon).font(.system(size: 13, weight: .semibold)).foregroundStyle(Look.cyan)
                Spacer()
                Button(action: back) { Image(systemName: "arrow.down.backward.square") }
                    .buttonStyle(.plain).foregroundStyle(.secondary).help("Rimetti nella plancia")
            }
            .padding(.leading, 78).padding(.trailing, 12).padding(.top, 7).padding(.bottom, 6)
            SectionBody(section: section, compact: true)
        }
        .frame(minWidth: 300, minHeight: 240)
        .background(Look.background.opacity(0.97).ignoresSafeArea())
        .tint(Look.cyan)
        .preferredColorScheme(.dark)
    }
}

// MARK: The one quick menu (stand-in for robot / satellite / sphere / menu-bar icon)

struct QuickMenuDemo: View {
    var open: (Area) -> Void
    var body: some View {
        VStack(spacing: 8) {
            ZStack {
                Circle().fill(Look.background).frame(width: 110, height: 110)
                Circle().stroke(Look.cyan.opacity(0.6), lineWidth: 2).frame(width: 110, height: 110)
                Image(systemName: "figure.stand").font(.system(size: 44)).foregroundStyle(Look.cyan)
            }
            Text("Tasto destro qui").font(.caption).foregroundStyle(.secondary)
            Text("(lo stesso menu sull'icona in alto, sulla sfera e sul satellite)")
                .font(.caption2).foregroundStyle(.tertiary).multilineTextAlignment(.center)
        }
        .padding(16)
        .frame(width: 240, height: 210)
        .contentShape(Rectangle())
        .contextMenu {
            Button("Parla  ⌃⌥Spazio") {}
            Button("Scrivi  ⌃⌥T") {}
            Button("Nuova conversazione") {}
            Divider()
            Button("Apri plancia  ⌃⌥K") { open(.dialogue) }
            Menu("Sezioni") {
                ForEach(Area.allCases) { s in Button(s.title) { open(s) } }
            }
            Divider()
            Menu("Cervello") {
                Button("Locale") {}; Button("✓ Claude") {}; Button("ChatGPT") {}; Button("Gemini") {}
            }
            Button("Modalità privacy  ⌃⌥P") {}
            Divider()
            Menu("Robottino") {
                Button("Blocca nell'angolo in basso a destra") {}
                Button("Mostra nuvoletta") {}
                Button("Nuvoletta anche per le risposte a voce") {}
                Divider()
                Button("Diventa satellite") {}
                Button("Apri la sfera") {}
            }
            Divider()
            Button("Impostazioni…") {}
            Button("Esci da AL-76") { NSApp.terminate(nil) }
        }
        .background(Look.background.opacity(0.9).ignoresSafeArea())
        .preferredColorScheme(.dark)
    }
}

// MARK: App

final class Delegate: NSObject, NSApplicationDelegate {
    let model = Model()
    var main: NSWindow!
    var demo: NSPanel!
    var small: [Area: NSPanel] = [:]

    func applicationDidFinishLaunching(_ notification: Notification) {
        main = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 800, height: 580),
                        styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                        backing: .buffered, defer: false)
        main.title = "AL-76 · Plancia"
        main.titlebarAppearsTransparent = true
        main.titleVisibility = .hidden
        main.isMovableByWindowBackground = true
        main.backgroundColor = NSColor(red: 0.01, green: 0.04, blue: 0.08, alpha: 1)
        main.isReleasedWhenClosed = false
        main.contentView = NSHostingView(rootView: Plancia(model: model, detach: { [weak self] in self?.detach($0) }))
        main.center()
        main.orderFrontRegardless()

        demo = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 240, height: 210),
                       styleMask: [.titled, .closable, .nonactivatingPanel, .fullSizeContentView],
                       backing: .buffered, defer: false)
        demo.title = "Menu rapido"
        demo.titlebarAppearsTransparent = true
        demo.isReleasedWhenClosed = false
        demo.contentView = NSHostingView(rootView: QuickMenuDemo(open: { [weak self] s in
            self?.model.section = s
            self?.main.orderFrontRegardless()
        }))
        demo.setFrameOrigin(NSPoint(x: main.frame.maxX + 16, y: main.frame.minY))
        demo.orderFrontRegardless()

        // The three candidate menu-bar icons, side by side in the real menu bar, each with the one quick menu.
        for variant in RobotHeadIcon.Variant.allCases.reversed() {
            let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
            item.button?.image = RobotHeadIcon.image(variant)
            item.button?.toolTip = "Icona \(variant.rawValue)"
            item.menu = quickMenu(label: variant.rawValue)
            statusItems.append(item)
        }
    }

    var statusItems: [NSStatusItem] = []

    /// The one quick menu, as an NSMenu: the same rows the robot, satellite and sphere would show.
    func quickMenu(label: String) -> NSMenu {
        let menu = NSMenu()
        menu.autoenablesItems = false
        let head = NSMenuItem(title: "Icona \(label) · AL-76 in ascolto", action: nil, keyEquivalent: "")
        head.isEnabled = false
        menu.addItem(head)
        menu.addItem(.separator())
        menu.addItem(withTitle: "Parla  ⌃⌥Spazio", action: nil, keyEquivalent: "")
        menu.addItem(withTitle: "Scrivi  ⌃⌥T", action: nil, keyEquivalent: "")
        menu.addItem(withTitle: "Nuova conversazione", action: nil, keyEquivalent: "")
        menu.addItem(.separator())
        menu.addItem(Run("Apri plancia  ⌃⌥K") { [weak self] in self?.show(.dialogue) })
        let sections = NSMenu()
        for area in Area.allCases { sections.addItem(Run(area.title) { [weak self] in self?.show(area) }) }
        let sectionsItem = NSMenuItem(title: "Sezioni", action: nil, keyEquivalent: "")
        sectionsItem.submenu = sections
        menu.addItem(sectionsItem)
        menu.addItem(.separator())
        let brains = NSMenu()
        for (name, on) in [("Locale", false), ("Claude", true), ("ChatGPT", false), ("Gemini", false)] {
            let b = NSMenuItem(title: name, action: nil, keyEquivalent: ""); b.state = on ? .on : .off; brains.addItem(b)
        }
        let brainItem = NSMenuItem(title: "Cervello", action: nil, keyEquivalent: ""); brainItem.submenu = brains
        menu.addItem(brainItem)
        menu.addItem(withTitle: "Modalità privacy  ⌃⌥P", action: nil, keyEquivalent: "")
        menu.addItem(.separator())
        let robot = NSMenu()
        for t in ["Blocca nell'angolo in basso a destra", "Mostra nuvoletta", "Nuvoletta anche per le risposte a voce"] {
            robot.addItem(withTitle: t, action: nil, keyEquivalent: "")
        }
        robot.addItem(.separator())
        robot.addItem(withTitle: "Diventa satellite", action: nil, keyEquivalent: "")
        robot.addItem(withTitle: "Apri la sfera", action: nil, keyEquivalent: "")
        let robotItem = NSMenuItem(title: "Robottino", action: nil, keyEquivalent: ""); robotItem.submenu = robot
        menu.addItem(robotItem)
        menu.addItem(.separator())
        menu.addItem(withTitle: "Impostazioni…", action: nil, keyEquivalent: "")
        menu.addItem(Run("Chiudi l'anteprima") { NSApp.terminate(nil) })
        return menu
    }

    func show(_ area: Area) {
        model.section = area
        main.orderFrontRegardless()
    }

    func detach(_ section: Area) {
        if let panel = small[section] { panel.orderFrontRegardless(); return }
        let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 340, height: 420),
                            styleMask: [.titled, .closable, .resizable, .fullSizeContentView, .nonactivatingPanel],
                            backing: .buffered, defer: false)
        panel.titlebarAppearsTransparent = true
        panel.titleVisibility = .hidden
        panel.isMovableByWindowBackground = true
        panel.isReleasedWhenClosed = false
        panel.backgroundColor = .clear
        panel.contentView = NSHostingView(rootView: Detached(section: section) { [weak self, weak panel] in
            panel?.close()
            self?.small[section] = nil
            self?.model.detached.remove(section)
            self?.model.section = section
        })
        let offset = CGFloat(small.count) * 24
        panel.setFrameOrigin(NSPoint(x: main.frame.minX - 356 + offset, y: main.frame.maxY - 420 - offset))
        panel.orderFrontRegardless()
        small[section] = panel
        model.detached.insert(section)
    }
}

final class Run: NSMenuItem {
    private let block: () -> Void
    init(_ title: String, _ block: @escaping () -> Void) {
        self.block = block
        super.init(title: title, action: #selector(go), keyEquivalent: "")
        target = self
    }
    required init(coder: NSCoder) { fatalError() }
    @objc private func go() { block() }
}

// MARK: Menu-bar icon: the robot's head, second round (23 November: the first three were rejected)

enum RobotHeadIcon {
    enum Variant: String, CaseIterable {
        case faccia = "A faccia", casco = "B casco", arancio = "C arancio", arancioBordo = "D occhi ciano", occhi = "E occhi", profilo = "F contorno"
        var colored: Bool { self == .arancio || self == .arancioBordo }
    }

    static let orange = CGColor(srgbRed: 1.0, green: 0.36, blue: 0.10, alpha: 1)
    static let orangeLight = CGColor(srgbRed: 1.0, green: 0.55, blue: 0.32, alpha: 1)
    static let orangeDark = CGColor(srgbRed: 0.62, green: 0.18, blue: 0.04, alpha: 1)
    static let screenColor = CGColor(srgbRed: 0.06, green: 0.06, blue: 0.09, alpha: 1)

    // 18×18 points, y up. The real head: rounded helmet, wider on top, ridged band on top,
    // black screen low on the front with two oval eyes and a tiny "v" mouth, two fins behind slanting up.
    static let helmet = CGRect(x: 3.4, y: 2.4, width: 11.2, height: 12.2)
    static let screen = CGRect(x: 4.9, y: 3.9, width: 8.2, height: 7.0)
    static let eyes = [CGPoint(x: 7.15, y: 7.9), CGPoint(x: 10.85, y: 7.9)]

    static func helmetPath(_ r: CGRect = helmet) -> CGPath {
        CGPath(roundedRect: r, cornerWidth: 4.4, cornerHeight: 4.4, transform: nil)
    }

    static func fins() -> CGPath {
        let p = CGMutablePath()
        for side in [-1.0, 1.0] as [CGFloat] {
            var t = CGAffineTransform(translationX: 9 + side * 6.3, y: 10.2).rotated(by: side * -0.12)
            p.addPath(CGPath(roundedRect: CGRect(x: -0.8, y: -3.2, width: 1.6, height: 6.6), cornerWidth: 0.8, cornerHeight: 0.8, transform: &t))
        }
        return p
    }

    static func faceMarks(_ c: CGContext, eyeW: CGFloat = 2.0, eyeH: CGFloat = 2.5, mouth: Bool = true, mouthY: CGFloat = 5.4) {
        for e in eyes { c.fillEllipse(in: CGRect(x: e.x - eyeW / 2, y: e.y - eyeH / 2, width: eyeW, height: eyeH)) }
        if mouth {
            c.setLineWidth(0.8); c.setLineCap(.round); c.setLineJoin(.round)
            c.move(to: CGPoint(x: 8.2, y: mouthY + 0.6)); c.addLine(to: CGPoint(x: 9, y: mouthY)); c.addLine(to: CGPoint(x: 9.8, y: mouthY + 0.6))
            c.strokePath()
        }
    }

    static func image(_ variant: Variant, size: CGFloat = 18) -> NSImage {
        let image = NSImage(size: NSSize(width: size, height: size), flipped: false) { _ in
            guard let c = NSGraphicsContext.current?.cgContext else { return false }
            c.scaleBy(x: size / 18, y: size / 18)
            c.setFillColor(.black); c.setStrokeColor(.black)
            switch variant {
            case .faccia:
                // Only the screen with its expression, like the face the robot shows.
                let r = CGRect(x: 2.2, y: 3.2, width: 13.6, height: 11.0)
                c.setLineWidth(1.5)
                c.addPath(CGPath(roundedRect: r, cornerWidth: 3.8, cornerHeight: 3.8, transform: nil)); c.strokePath()
                c.translateBy(x: 0, y: 1.0)
                faceMarks(c, eyeW: 2.3, eyeH: 2.9, mouthY: 5.0)
            case .casco:
                c.addPath(fins()); c.fillPath()
                c.addPath(helmetPath()); c.addPath(CGPath(roundedRect: screen, cornerWidth: 2.6, cornerHeight: 2.6, transform: nil))
                c.fillPath(using: .evenOdd)
                c.setBlendMode(.clear)
                c.fill(CGRect(x: 4, y: 12.1, width: 10, height: 0.8))
                c.setBlendMode(.normal)
                faceMarks(c)
            case .arancio, .arancioBordo:
                c.setFillColor(orangeDark); c.addPath(fins()); c.fillPath()
                c.setFillColor(orange); c.addPath(helmetPath()); c.fillPath()
                c.setFillColor(orangeLight)
                c.fill(CGRect(x: 6.2, y: 11.9, width: 1.1, height: 2.6)); c.fill(CGRect(x: 8.45, y: 11.9, width: 1.1, height: 2.6)); c.fill(CGRect(x: 10.7, y: 11.9, width: 1.1, height: 2.6))
                c.setFillColor(screenColor)
                c.addPath(CGPath(roundedRect: screen, cornerWidth: 2.6, cornerHeight: 2.6, transform: nil)); c.fillPath()
                let eye: CGColor = variant == .arancioBordo ? CGColor(srgbRed: 0.31, green: 0.86, blue: 1.0, alpha: 1) : .white
                c.setFillColor(eye); c.setStrokeColor(eye)
                faceMarks(c)
            case .occhi:
                // The expression alone, big: two eyes and the smile.
                c.translateBy(x: 0, y: 0.6)
                for x in [5.6, 12.4] as [CGFloat] { c.fillEllipse(in: CGRect(x: x - 2.0, y: 7.2, width: 4.0, height: 5.0)) }
                c.setLineWidth(1.4); c.setLineCap(.round); c.setLineJoin(.round)
                c.move(to: CGPoint(x: 7.4, y: 4.6)); c.addLine(to: CGPoint(x: 9, y: 3.4)); c.addLine(to: CGPoint(x: 10.6, y: 4.6)); c.strokePath()
            case .profilo:
                c.setLineWidth(1.3)
                c.addPath(fins()); c.fillPath()
                c.setBlendMode(.clear); c.addPath(helmetPath(helmet.insetBy(dx: -0.9, dy: -0.9))); c.fillPath(); c.setBlendMode(.normal)
                c.addPath(helmetPath(helmet.insetBy(dx: 0.65, dy: 0.65))); c.strokePath()
                c.addPath(CGPath(roundedRect: screen.insetBy(dx: 0.2, dy: 0.2), cornerWidth: 2.4, cornerHeight: 2.4, transform: nil)); c.fillPath()
                c.setBlendMode(.clear); faceMarks(c); c.setBlendMode(.normal)
            }
            return true
        }
        image.isTemplate = !variant.colored
        return image
    }

    /// The six variants big, and at true size on a light and a dark menu bar.
    static func sheet(to path: String) {
        let variants = Variant.allCases
        let big: CGFloat = 126, cell: CGFloat = 160, rowH: CGFloat = 170
        let width = cell * CGFloat(variants.count)
        let out = NSImage(size: NSSize(width: width, height: rowH * 2 + 2 * 34 + 30), flipped: false) { _ in
            NSColor(white: 0.97, alpha: 1).setFill(); NSRect(x: 0, y: 0, width: width, height: 1000).fill()
            for (row, dark) in [(0, true), (1, false)] {
                let y0 = CGFloat(row) * (rowH + 34)
                (dark ? NSColor(white: 0.13, alpha: 1) : NSColor(white: 0.90, alpha: 1)).setFill()
                NSRect(x: 0, y: y0, width: width, height: rowH + 34).fill()
                for (i, v) in variants.enumerated() {
                    let x = CGFloat(i) * cell + (cell - big) / 2
                    draw(v, in: NSRect(x: x, y: y0 + 34 + 20, width: big, height: big), dark: dark)
                    // True size (18 pt, drawn at 2×) under the big one.
                    draw(v, in: NSRect(x: CGFloat(i) * cell + cell / 2 - 18, y: y0 + 4, width: 36, height: 36), dark: dark)
                }
            }
            for (i, v) in variants.enumerated() {
                (v.rawValue as NSString).draw(at: NSPoint(x: CGFloat(i) * cell + 16, y: 2 * (rowH + 34) + 6),
                                              withAttributes: [.font: NSFont.systemFont(ofSize: 16, weight: .medium), .foregroundColor: NSColor.black])
            }
            return true
        }
        let rep = NSBitmapImageRep(data: out.tiffRepresentation!)!
        try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: path))
    }

    private static func draw(_ v: Variant, in rect: NSRect, dark: Bool) {
        let img = image(v, size: rect.width)
        if v.colored { img.draw(in: rect); return }
        let tinted = NSImage(size: img.size, flipped: false) { r in
            img.draw(in: r)
            (dark ? NSColor.white : NSColor.black).set()
            r.fill(using: .sourceAtop)
            return true
        }
        tinted.draw(in: rect)
    }
}

if let i = CommandLine.arguments.firstIndex(of: "--icons"), i + 1 < CommandLine.arguments.count {
    RobotHeadIcon.sheet(to: CommandLine.arguments[i + 1])
    exit(0)
}

let app = NSApplication.shared
let delegate = Delegate()
app.delegate = delegate
app.setActivationPolicy(.regular)
app.appearance = NSAppearance(named: .darkAqua)
app.run()
