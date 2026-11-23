import AppKit
import Carbon
import SwiftUI

/// The Plancia (1.30.81, 23 November, user request): one window in the style of the Settings that takes
/// the place of the seven old panels. Four sections in a sidebar; each can be detached into a small
/// window to keep beside the robot. It opens from the one quick menu (menu-bar icon, robot, satellite,
/// sphere), from ⌃⌥K and from the "Plancia" menu on the left. The ⌃⌥T typing bar stays as it was.
enum PlanciaArea: String, CaseIterable, Identifiable {
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
        case .dialogue: return "Cosa ci siamo detti. Puoi anche scrivermi da qui; per una frase al volo c'è la barra ⌃⌥T."
        case .day: return "Le sessioni di studio e lavoro, i resoconti e le schede da ripassare."
        case .status: return "Come stanno le mie parti, e cosa è successo di recente."
        case .thought: return "Cosa faccio mentre rispondo: strade scelte, strumenti, tempi."
        }
    }
}

@MainActor
final class Plancia: NSObject, ObservableObject, NSWindowDelegate {
    @Published var area: PlanciaArea {
        didSet { UserDefaults.standard.set(area.rawValue, forKey: Self.areaKey) }
    }
    @Published private(set) var detached: Set<PlanciaArea> = []
    private let assistant: Assistant
    /// Where AL-76 is now (sphere, satellite or robot): the first time the Plancia opens beside it.
    var anchor: () -> NSRect? = { nil }
    private var window: NSWindow?
    private var small: [PlanciaArea: NSPanel] = [:]
    private var hotKey: HotKey?

    private static let areaKey = "planciaArea"
    private static let openKey = "planciaOpen"
    private static let detachedKey = "planciaDetached"
    static let size = NSSize(width: 820, height: 580)

    /// The app's Plancia, for the menus.
    private(set) static weak var main: Plancia?

    init(assistant: Assistant) {
        self.assistant = assistant
        area = UserDefaults.standard.string(forKey: Self.areaKey).flatMap(PlanciaArea.init(rawValue:)) ?? .dialogue
        super.init()
        Self.main = self
        hotKey = HotKey(keyCode: kVK_ANSI_K) { [weak self] in self?.flip() }
        // The seven old panels (until 1.30.80) are gone: their saved state is no longer needed.
        UserDefaults.standard.removeObject(forKey: "openPanels")
    }

    var isOpen: Bool { window?.isVisible == true }

    /// Opens the Plancia in front, on a section if given. Brings AL-76 forward: it is asked for
    /// (`activate: false` only for the test channel, which must not take the keyboard).
    func open(_ section: PlanciaArea? = nil, activate: Bool = true) {
        if let section {
            // A detached section comes forward in its own small window.
            if let panel = small[section] { panel.orderFrontRegardless(); return }
            area = section
        }
        let window = window ?? makeWindow()
        self.window = window
        if activate {
            window.makeKeyAndOrderFront(nil)
            NSApp.activate()
        } else {
            window.orderFrontRegardless()
        }
        UserDefaults.standard.set(true, forKey: Self.openKey)
    }

    /// ⌃⌥K: opens it, or closes it when it is already the window in front.
    func flip() {
        if let window, window.isVisible, window.isKeyWindow { window.close() } else { open() }
    }

    func close() { window?.close() }

    /// The Plancia and every detached section (test channel).
    func closeAll() {
        small.values.forEach { $0.close() }
        close()
    }

    /// The Plancia and the detached sections that were open when AL-76 last quit, without taking the keyboard.
    func restore() {
        let names = UserDefaults.standard.stringArray(forKey: Self.detachedKey) ?? []
        names.compactMap(PlanciaArea.init(rawValue:)).forEach { detach($0) }
        if UserDefaults.standard.bool(forKey: Self.openKey) {
            let window = window ?? makeWindow()
            self.window = window
            window.orderFrontRegardless()
        }
    }

    func windowWillClose(_ notification: Notification) {
        guard let closing = notification.object as? NSWindow else { return }
        if closing === window {
            UserDefaults.standard.set(false, forKey: Self.openKey)
        } else if let section = small.first(where: { $0.value === closing })?.key {
            small[section] = nil
            detached.remove(section)
            saveDetached()
        }
    }

    // MARK: Detached sections

    /// The section in a small window of its own, to keep beside the robot.
    func detach(_ section: PlanciaArea) {
        if let panel = small[section] { panel.orderFrontRegardless(); return }
        let panel = NSPanel(contentRect: NSRect(x: 0, y: 0, width: 360, height: 440),
                            styleMask: [.titled, .closable, .resizable, .fullSizeContentView, .nonactivatingPanel],
                            backing: .buffered, defer: false)
        panel.title = "AL-76 · \(section.title)"
        panel.titlebarAppearsTransparent = true
        panel.titleVisibility = .hidden
        panel.standardWindowButton(.miniaturizeButton)?.isHidden = true
        panel.standardWindowButton(.zoomButton)?.isHidden = true
        panel.isMovableByWindowBackground = true
        panel.isReleasedWhenClosed = false
        panel.hidesOnDeactivate = false
        panel.becomesKeyOnlyIfNeeded = true
        panel.collectionBehavior = [.moveToActiveSpace, .fullScreenAuxiliary]
        panel.appearance = NSAppearance(named: .darkAqua)
        panel.backgroundColor = NSColor(HUD.background).withAlphaComponent(0.97)
        panel.minSize = NSSize(width: 300, height: 220)
        panel.delegate = self
        panel.contentView = NSHostingView(rootView: PlanciaDetachedView(section: section, assistant: assistant) { [weak self] in
            self?.attach(section)
        })
        let name = "AL76Plancia.\(section.rawValue)"
        if !panel.setFrameUsingName(name) { panel.setFrameOrigin(detachedOrigin(panel.frame.size)) }
        panel.setFrameAutosaveName(name)
        panel.orderFrontRegardless()
        small[section] = panel
        detached.insert(section)
        saveDetached()
    }

    /// Back into the Plancia, on that section.
    func attach(_ section: PlanciaArea) {
        small[section]?.close()
        open(section)
    }

    private func saveDetached() {
        UserDefaults.standard.set(detached.map(\.rawValue).sorted(), forKey: Self.detachedKey)
    }

    // MARK: Windows

    private func makeWindow() -> NSWindow {
        let window = NSWindow(contentRect: NSRect(origin: .zero, size: Self.size),
                              styleMask: [.titled, .closable, .miniaturizable, .resizable, .fullSizeContentView],
                              backing: .buffered, defer: false)
        window.title = "AL-76 · Plancia"
        window.titlebarAppearsTransparent = true
        window.titleVisibility = .hidden
        window.isMovableByWindowBackground = true
        window.isReleasedWhenClosed = false
        window.appearance = NSAppearance(named: .darkAqua)
        window.backgroundColor = NSColor(HUD.background)
        window.minSize = NSSize(width: 660, height: 460)
        window.collectionBehavior = [.moveToActiveSpace, .fullScreenAuxiliary]
        window.delegate = self
        let host = NSHostingView(rootView: PlanciaView(plancia: self, assistant: assistant))
        host.sizingOptions = []
        window.contentView = host
        window.setContentSize(Self.size)
        let name = "AL76Plancia"
        if !window.setFrameUsingName(name) { window.setFrameOrigin(firstOrigin(window.frame.size)) }
        window.setFrameAutosaveName(name)
        return window
    }

    /// The first time: beside AL-76, on the side with room, otherwise in the middle of the display.
    private func firstOrigin(_ size: NSSize) -> NSPoint {
        let area = (NSScreen.main ?? NSScreen.screens.first)?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        var origin = NSPoint(x: area.midX - size.width / 2, y: area.midY - size.height / 2)
        if let anchor = anchor() {
            if anchor.minX - size.width - 16 >= area.minX {
                origin.x = anchor.minX - size.width - 16
            } else if anchor.maxX + 16 + size.width <= area.maxX {
                origin.x = anchor.maxX + 16
            }
            origin.y = min(max(anchor.maxY - size.height, area.minY), area.maxY - size.height)
        }
        return origin
    }

    /// A detached section: just left of AL-76, stacked a little for each one already open.
    private func detachedOrigin(_ size: NSSize) -> NSPoint {
        let area = (NSScreen.main ?? NSScreen.screens.first)?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900)
        let step = CGFloat(small.count) * 28
        let anchor = anchor() ?? NSRect(x: area.maxX - 200, y: area.minY, width: 200, height: 260)
        var origin = NSPoint(x: anchor.minX - size.width - 12 - step, y: anchor.maxY - size.height + 120 - step)
        origin.x = min(max(origin.x, area.minX), area.maxX - size.width)
        origin.y = min(max(origin.y, area.minY), area.maxY - size.height)
        return origin
    }
}

// MARK: - Views

/// The window: sidebar with AL-76's state and the sections, the section's title and intro,
/// the brain chips, then the section itself. Same layout as the Settings.
struct PlanciaView: View {
    @ObservedObject var plancia: Plancia
    @ObservedObject var assistant: Assistant

    var body: some View {
        HStack(spacing: 0) {
            sidebar
            Divider().opacity(0.4)
            VStack(spacing: 0) {
                header
                if plancia.detached.contains(plancia.area) {
                    detachedNote
                } else {
                    PlanciaSection(area: plancia.area, assistant: assistant)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        }
        .background(HUD.background.ignoresSafeArea())
        .tint(HUD.cyan)
        .preferredColorScheme(.dark)
    }

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 2) {
            PlanciaBadge(assistant: assistant)
                .padding(.horizontal, 8).padding(.bottom, 12)
            ForEach(PlanciaArea.allCases) { item in
                Button { plancia.area = item } label: {
                    HStack {
                        Label(item.title, systemImage: item.icon)
                        Spacer()
                        if plancia.detached.contains(item) {
                            Image(systemName: "rectangle.on.rectangle").font(.caption2).foregroundStyle(.secondary)
                                .help("Aperta in una finestrella")
                        }
                    }
                    .font(.system(size: 13, weight: item == plancia.area ? .semibold : .regular))
                    .foregroundStyle(item == plancia.area ? HUD.cyan : .primary)
                    .padding(.horizontal, 10).padding(.vertical, 7)
                    .background(item == plancia.area ? HUD.cyan.opacity(0.12) : .clear, in: RoundedRectangle(cornerRadius: 8))
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
            Spacer()
            Divider().opacity(0.3).padding(.vertical, 6)
            SettingsLink {
                Label("Impostazioni…", systemImage: "gearshape")
                    .font(.system(size: 13)).foregroundStyle(.secondary)
                    .padding(.horizontal, 10).padding(.vertical, 7)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
        }
        .padding(.horizontal, 10).padding(.top, 34).padding(.bottom, 12)
        .frame(width: 200)
        .background(Color.white.opacity(0.03))
    }

    private var header: some View {
        HStack(alignment: .top, spacing: 16) {
            VStack(alignment: .leading, spacing: 6) {
                Text(plancia.area.title).font(.system(size: 22, weight: .semibold))
                Text(plancia.area.intro).font(.callout).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Spacer(minLength: 0)
            VStack(alignment: .trailing, spacing: 8) {
                BrainChips(assistant: assistant)
                if !plancia.detached.contains(plancia.area) {
                    Button { plancia.detach(plancia.area) } label: {
                        Label("Stacca in finestrella", systemImage: "arrow.up.forward.square")
                            .font(.caption).foregroundStyle(HUD.cyan.opacity(0.85))
                    }
                    .buttonStyle(.plain)
                    .help("Apre questa sezione in una finestra piccola, da tenere accanto al robottino")
                }
            }
        }
        .padding(.horizontal, 24).padding(.top, 34).padding(.bottom, 10)
    }

    private var detachedNote: some View {
        VStack(spacing: 12) {
            Image(systemName: "rectangle.on.rectangle").font(.system(size: 28)).foregroundStyle(HUD.cyan.opacity(0.6))
            Text("\(plancia.area.title) è aperta in una finestrella.").foregroundStyle(.secondary)
            HStack {
                Button("Mostrala") { plancia.open(plancia.area) }
                Button("Rimettila qui") { plancia.attach(plancia.area) }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

/// AL-76's state at the top of the sidebar: the link between the window and the character.
struct PlanciaBadge: View {
    @ObservedObject var assistant: Assistant
    @State private var pulse = false

    var body: some View {
        HStack(spacing: 10) {
            ZStack {
                Circle().stroke(color.opacity(0.35), lineWidth: 1).frame(width: 28, height: 28)
                    .scaleEffect(pulse ? 1.15 : 0.95).opacity(pulse ? 0.2 : 0.8)
                Circle().fill(color).frame(width: 10, height: 10).shadow(color: color, radius: 5)
            }
            .frame(width: 30, height: 30)
            VStack(alignment: .leading, spacing: 1) {
                Text("AL-76").font(HUD.font(15, .bold)).tracking(2).foregroundStyle(HUD.ice)
                Text(line).font(.caption).foregroundStyle(.secondary).lineLimit(1)
            }
        }
        .onAppear { withAnimation(.easeInOut(duration: 1.6).repeatForever(autoreverses: true)) { pulse = true } }
    }

    private var color: Color {
        if assistant.privacyMode { return HUD.privacy }
        return assistant.state.isError ? HUD.amber : HUD.cyan
    }

    private var line: String {
        if assistant.privacyMode { return "Privacy · ⌃⌥P per riattivare" }
        let brain = assistant.config.noAI ? "senza IA" : (assistant.answering ?? assistant.brain).title
        return QuickMenu.stateWord(assistant.state) + " · " + brain.prefix(1).uppercased() + brain.dropFirst()
    }
}

/// The brain in use, one tap to change it; with Claude, its model too. They replace the old "Protocollo" panel.
struct BrainChips: View {
    @ObservedObject var assistant: Assistant

    var body: some View {
        VStack(alignment: .trailing, spacing: 6) {
            HStack(spacing: 6) {
                chip(.local, "Locale")
                chip(.claude, "Claude")
                chip(.chatgpt, "GPT")
                chip(.gemini, "Gemini")
                // 1.30.84: no model at all, only the known commands. Lit = AI off; the brain chips dim.
                PlanciaChip(text: "Senza IA", on: assistant.config.noAI) {
                    assistant.setArtificialIntelligence(assistant.config.noAI)
                }
            }
            if assistant.brain == .claude, !assistant.config.noAI {
                HStack(spacing: 6) {
                    ForEach(Assistant.claudeModels, id: \.self) { model in
                        PlanciaChip(text: model.capitalized, on: assistant.claudeModel == model, small: true) {
                            assistant.setClaudeModel(model)
                        }
                    }
                }
            }
        }
    }

    /// Lit: the chosen brain, or the one answering right now (with a dot when it stands in for the chosen one).
    private func chip(_ brain: Assistant.Brain, _ title: String) -> some View {
        let standIn = assistant.answering == brain && assistant.brain != brain
        return PlanciaChip(text: standIn ? "● " + title : title, on: (assistant.answering ?? assistant.brain) == brain) {
            assistant.setBrain(brain)
        }
        .disabled(assistant.config.noAI)
        .opacity(assistant.config.noAI ? 0.4 : 1)
    }
}

struct PlanciaChip: View {
    let text: String
    let on: Bool
    var small = false
    let action: () -> Void

    var body: some View {
        Button(action: action) {
            Text(text).font(HUD.font(small ? 10 : 11, .bold)).tracking(1).lineLimit(1).fixedSize()
                .padding(.horizontal, small ? 7 : 9).padding(.vertical, small ? 3 : 4)
                .foregroundStyle(on ? HUD.background : HUD.cyan)
                .background(on ? HUD.cyan : HUD.cyan.opacity(0.07), in: Capsule())
                .overlay(Capsule().stroke(HUD.cyan.opacity(on ? 0 : 0.4), lineWidth: 1))
        }
        .buttonStyle(.plain)
    }
}

/// A detached section: its name, the button to put it back, then the section itself.
struct PlanciaDetachedView: View {
    let section: PlanciaArea
    @ObservedObject var assistant: Assistant
    var back: () -> Void

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Label(section.title, systemImage: section.icon)
                    .font(.system(size: 13, weight: .semibold)).foregroundStyle(HUD.cyan)
                Spacer()
                Button(action: back) { Image(systemName: "arrow.down.backward.square") }
                    .buttonStyle(.plain).foregroundStyle(.secondary).help("Rimetti nella plancia")
            }
            .padding(.leading, 64).padding(.trailing, 12).padding(.top, 7).padding(.bottom, 6)
            PlanciaSection(area: section, assistant: assistant, compact: true)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
        .background(HUD.background.opacity(0.97).ignoresSafeArea())
        .tint(HUD.cyan)
        .preferredColorScheme(.dark)
    }
}

/// What a section shows, the same in the Plancia and in a detached window.
struct PlanciaSection: View {
    let area: PlanciaArea
    @ObservedObject var assistant: Assistant
    var compact = false

    var body: some View {
        switch area {
        case .dialogue: DialogueSection(assistant: assistant, compact: compact)
        case .day: DaySection(activity: assistant.activity, flashcards: assistant.flashcards, compact: compact)
        case .status: StatusSection(assistant: assistant, printer: assistant.printer)
        case .thought: ThoughtSection(assistant: assistant, compact: compact)
        }
    }
}

// MARK: Dialogo

/// The conversation, both sides; it scrolls down by itself while AL-76 answers. The field under it
/// sends typed text the same way as the ⌃⌥T bar.
struct DialogueSection: View {
    @ObservedObject var assistant: Assistant
    var compact: Bool
    @State private var draft = ""

    var body: some View {
        VStack(spacing: 0) {
            ScrollViewReader { proxy in
                ScrollView(.vertical) {
                    LazyVStack(alignment: .leading, spacing: 12) {
                        if assistant.conversation.isEmpty {
                            Text("Nessuna frase in questa conversazione. Di' «Ehi Al» o scrivi qui sotto.")
                                .font(.callout).foregroundStyle(.secondary).padding(.top, 8)
                        }
                        ForEach(assistant.conversation) { exchange in
                            if !exchange.user.isEmpty { row(user: true, text: exchange.user) }
                            if !exchange.reply.isEmpty { row(user: false, text: exchange.reply) }
                        }
                        Color.clear.frame(height: 1).id("bottom")
                    }
                    .padding(.horizontal, compact ? 14 : 24).padding(.vertical, 8)
                }
                .scrollIndicators(.visible)
                .onChange(of: assistant.conversation.count) { _, _ in
                    withAnimation(.easeOut(duration: 0.25)) { proxy.scrollTo("bottom", anchor: .bottom) }
                }
                .onChange(of: assistant.conversation.last?.reply) { _, _ in proxy.scrollTo("bottom", anchor: .bottom) }
                .onAppear { proxy.scrollTo("bottom", anchor: .bottom) }
            }
            HStack(spacing: 8) {
                TextField("Scrivi ad AL-76…", text: $draft)
                    .textFieldStyle(.plain)
                    .onSubmit(send)
                    .padding(.horizontal, 12).padding(.vertical, 8)
                    .background(HUD.cyan.opacity(0.06), in: RoundedRectangle(cornerRadius: 10))
                    .overlay(RoundedRectangle(cornerRadius: 10).stroke(HUD.cyan.opacity(0.3), lineWidth: 1))
                Button { assistant.pushToTalk() } label: { Image(systemName: "mic.fill") }
                    .help("Parla (⌃⌥Spazio)")
                if !compact {
                    Button("Nuova conversazione") { assistant.resetConversation() }
                }
            }
            .padding(compact ? 10 : 16)
            .background(Color.white.opacity(0.02))
        }
    }

    private func send() {
        if assistant.typed(draft) { draft = "" }
    }

    private func row(user: Bool, text: String) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Text(user ? "Tu" : "AL-76")
                .font(HUD.font(11, .bold)).tracking(1)
                .foregroundStyle(user ? Color.secondary : HUD.cyan)
                .frame(width: 44, alignment: .leading)
            Text(text).font(.system(size: 13))
                .foregroundStyle(user ? Color.primary.opacity(0.85) : HUD.ice)
                .textSelection(.enabled)
                .fixedSize(horizontal: false, vertical: true)
                .frame(maxWidth: .infinity, alignment: .leading)
        }
    }
}

// MARK: Stato

/// The old "Moduli" and "Registro" panels together.
struct StatusSection: View {
    @ObservedObject var assistant: Assistant
    let printer: BambuPrinter
    @ObservedObject private var requests = RequestLog.shared

    var body: some View {
        Form {
            requestsSection
            Section("Moduli") {
                module("waveform", "Chiamata «Ehi Al»", assistant.wakeStatus)
                module("ear", "Udito", assistant.sttStatus)
                module("cpu", "Nucleo locale", assistant.brainStatus)
                module("speaker.wave.2", "Voce", assistant.voiceStatus)
                module("brain", assistant.brain == .local ? "Claude" : assistant.brain.title,
                       assistant.config.noAI ? "IA SPENTA · SOLO COMANDI" : assistant.online ? (assistant.brain == .chatgpt || assistant.brain == .gemini
                                           ? (assistant.cloudStatus[assistant.brain] ?? "—") : assistant.claudeStatus)
                                        : "Offline · risponde Gemma")
                if printer.isConfigured {
                    TimelineView(.periodic(from: .now, by: 5)) { _ in
                        module("printer", "Stampante", printer.hudStatus)
                    }
                }
            }
            Section("Registro") {
                if assistant.log.isEmpty {
                    Text("Nessun evento").foregroundStyle(.secondary)
                }
                ForEach(Array(assistant.log.suffix(60).reversed().enumerated()), id: \.offset) { _, line in
                    Text(line).font(HUD.mono(11)).foregroundStyle(HUD.cyan.opacity(0.85))
                        .textSelection(.enabled)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
        .formStyle(.grouped)
        .scrollContentBackground(.hidden)
    }

    /// 1.30.84: how many requests were answered without a model, and what went to the model this week.
    @ViewBuilder private var requestsSection: some View {
        let today = requests.day()
        let week = requests.period(days: 7)
        Section {
            figures("Oggi", today)
            figures("Ultimi 7 giorni", week)
            if assistant.config.noAI {
                Text("IA spenta: rispondo solo con i comandi che conosco. Si riaccende da Impostazioni › Cervello o dicendo «riattiva l'IA».")
                    .font(.callout).foregroundStyle(HUD.amber)
            }
        } header: {
            Text("Senza modello")
        } footer: {
            Text("Obiettivo: almeno il 60% delle richieste senza Claude né Gemma.").font(.caption).foregroundStyle(.secondary)
        }
        let groups = requests.modelGroups(days: 7)
        if !groups.isEmpty {
            Section {
                ForEach(groups) { group in
                    VStack(alignment: .leading, spacing: 3) {
                        HStack {
                            Text(group.name.prefix(1).uppercased() + group.name.dropFirst())
                            Spacer()
                            Text("\(group.count)").font(HUD.font(12, .bold)).foregroundStyle(HUD.ice.opacity(0.85))
                        }
                        ForEach(group.examples, id: \.self) { example in
                            Text("«\(example)»").font(.caption).foregroundStyle(.secondary).lineLimit(1).textSelection(.enabled)
                        }
                    }
                }
            } header: {
                Text("Andate al modello · 7 giorni")
            } footer: {
                Text("Raggruppate per lo strumento che il modello ha usato: le più frequenti sono le prossime da rendere comandi diretti.")
                    .font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    private func figures(_ title: String, _ day: RequestLog.Day) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 10) {
            Text(title).frame(width: 110, alignment: .leading)
            if let share = day.modelFree {
                Text("\(share)%").font(HUD.font(15, .bold)).foregroundStyle(share >= 60 ? Color.green.opacity(0.9) : HUD.amber)
                Text(Self.breakdown(day)).font(.caption).foregroundStyle(.secondary).lineLimit(2)
            } else {
                Text("nessuna richiesta").foregroundStyle(.secondary)
            }
            Spacer()
        }
    }

    static func breakdown(_ day: RequestLog.Day) -> String {
        var parts = ["\(day.instant) istantanee", "\(day.direct) dirette", "\(day.model) al modello"]
        if day.withoutAI > 0 { parts.append("\(day.withoutAI) non fatte senza IA") }
        if let average = day.averageModelSeconds { parts.append(String(format: "modello in media %.1f s", average)) }
        return parts.joined(separator: " · ")
    }

    private func module(_ icon: String, _ name: String, _ value: String) -> some View {
        HStack(spacing: 10) {
            Image(systemName: icon).frame(width: 20).foregroundStyle(HUD.cyan)
            Text(name)
            Spacer()
            // The statuses are written for the HUD (capitals, names, acronyms): shown as they are, in its font.
            Text(value).font(HUD.font(12, .bold)).tracking(0.5).foregroundStyle(HUD.ice.opacity(0.85))
                .multilineTextAlignment(.trailing).lineLimit(2)
            Circle().fill(Self.healthy(value) ? Color.green.opacity(0.85) : HUD.amber).frame(width: 7, height: 7)
        }
    }

    /// Amber when the status says something is missing, off or failing.
    static func healthy(_ value: String) -> Bool {
        let v = value.lowercased()
        if v.isEmpty || v == "—" { return false }
        return !["errore", "error", "offline", "assente", "mancante", "non ", "spent", "disattiv", "fallit", "scollegat", "irraggiungibil"]
            .contains { v.contains($0) }
    }
}

// MARK: Pensiero

/// A terminal of AL-76's observable activity: route chosen, tools run, searches, files, sources, timings.
struct ThoughtSection: View {
    @ObservedObject var assistant: Assistant
    var compact: Bool

    var body: some View {
        ScrollViewReader { proxy in
            ScrollView(.vertical) {
                LazyVStack(alignment: .leading, spacing: 3) {
                    if assistant.thoughts.isEmpty {
                        Text("> in attesa di una richiesta_").font(HUD.mono(11)).foregroundStyle(HUD.cyan.opacity(0.4))
                    }
                    ForEach(assistant.thoughts) { line in
                        Text("\(line.time) \(line.message)").font(HUD.mono(11))
                            .foregroundStyle(Self.color(line.message))
                            .fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
                    }
                    if [.thinking, .acting].contains(assistant.state) {
                        Text("▌").font(HUD.mono(11)).foregroundStyle(HUD.ice)
                    }
                    Color.clear.frame(height: 1).id("end")
                }
                .frame(maxWidth: .infinity, alignment: .topLeading)
                .padding(12)
            }
            .background(Color.black.opacity(0.45), in: RoundedRectangle(cornerRadius: 10))
            .padding(.horizontal, compact ? 10 : 24).padding(.bottom, compact ? 10 : 20)
            .onChange(of: assistant.thoughts.last?.id) { _, _ in proxy.scrollTo("end", anchor: .bottom) }
            .onAppear { proxy.scrollTo("end", anchor: .bottom) }
        }
    }

    private static func color(_ body: String) -> Color {
        if body.hasPrefix("✕") { return HUD.amber }
        if body.hasPrefix("»") { return HUD.ice }
        if body.hasPrefix("←") { return HUD.cyan.opacity(0.55) }
        if body.hasPrefix("✓") { return Color.green.opacity(0.8) }
        return HUD.cyan.opacity(0.9)
    }
}
