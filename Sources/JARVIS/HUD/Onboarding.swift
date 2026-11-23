import AVFoundation
import Combine
import AppKit
import Contacts
import EventKit
import ServiceManagement
import SwiftUI

/// The first-run setup (v1.30.67): on the first launch after download, a guided introduction that sets up
/// what the original user and Claude built up over time — who you are, what you study, which brain,
/// the macOS permissions one at a time with the reason for each, the voice. Every step can also be
/// reopened later from the menu ("Configurazione guidata…"); everything stays changeable in Settings.
@MainActor
final class OnboardingWindow: NSObject, NSWindowDelegate {
    private let assistant: Assistant
    private var window: NSWindow?

    init(assistant: Assistant) {
        self.assistant = assistant
    }

    func show() {
        if let window {
            window.makeKeyAndOrderFront(nil)
            NSApp.activate()
            return
        }
        let window = NSWindow(contentRect: NSRect(origin: .zero, size: OnboardingView.size),
                              styleMask: [.titled, .closable, .fullSizeContentView], backing: .buffered, defer: false)
        window.title = "Benvenuto in AL-76"
        window.titlebarAppearsTransparent = true
        window.titleVisibility = .hidden
        window.isMovableByWindowBackground = true
        window.isReleasedWhenClosed = false
        window.appearance = NSAppearance(named: .darkAqua)
        window.backgroundColor = NSColor(HUD.background)
        window.delegate = self
        window.contentView = NSHostingView(rootView: OnboardingView(assistant: assistant) { [weak self] in self?.window?.close() })
        window.center()
        self.window = window
        window.makeKeyAndOrderFront(nil)
        NSApp.activate()
    }

    func windowWillClose(_ notification: Notification) {
        window = nil
    }
}

struct OnboardingView: View {
    static let size = NSSize(width: 680, height: 600)

    enum Step: Int, CaseIterable {
        case welcome, you, study, brain, permissions, voice, extras, done

        var title: String {
            switch self {
            case .welcome: return "Benvenuto"
            case .you: return "Chi sei"
            case .study: return "Studio"
            case .brain: return "Cervello"
            case .permissions: return "Permessi"
            case .voice: return "Voce"
            case .extras: return "Il resto"
            case .done: return "Pronto"
            }
        }
    }

    @ObservedObject var assistant: Assistant
    let onClose: () -> Void
    @State private var step = Step.welcome
    @State private var draft = JarvisConfig()
    @State private var loaded = false

    var body: some View {
        VStack(spacing: 0) {
            header
            Group {
                switch step {
                case .welcome: WelcomeStep()
                case .you: YouStep(draft: $draft)
                case .study: StudyStep(assistant: assistant, draft: $draft)
                case .brain: BrainStep(draft: $draft)
                case .permissions: PermissionsStep()
                case .voice: VoiceStep(assistant: assistant, voicePrint: assistant.voicePrint)
                case .extras: ExtrasStep(draft: $draft)
                case .done: DoneStep(assistant: assistant, voicePrint: assistant.voicePrint, draft: draft)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
            footer
        }
        .frame(width: Self.size.width, height: Self.size.height)
        .background(HUD.background)
        .tint(HUD.cyan)
        .preferredColorScheme(.dark)
        .onAppear(perform: load)
    }

    // MARK: Header and footer

    private var header: some View {
        HStack(spacing: 6) {
            ForEach(Step.allCases, id: \.self) { item in
                VStack(spacing: 5) {
                    Capsule()
                        .fill(item.rawValue <= step.rawValue ? HUD.cyan : Color.white.opacity(0.14))
                        .frame(height: 3)
                    Text(item.title)
                        .font(.system(size: 10, weight: item == step ? .semibold : .regular))
                        .foregroundStyle(item == step ? HUD.cyan : .secondary)
                }
            }
        }
        .padding(.horizontal, 24)
        .padding(.top, 34)
        .padding(.bottom, 8)
    }

    private var footer: some View {
        HStack {
            if step == .welcome {
                Button("Salta, configuro dopo") { finish() }
                    .buttonStyle(.link)
                    .foregroundStyle(.secondary)
            } else {
                Button("Indietro") { move(-1) }
            }
            Spacer()
            if step == .done {
                Button("Fine") { finish() }
                    .keyboardShortcut(.defaultAction)
            } else {
                Button(step == .welcome ? "Cominciamo" : "Avanti") { move(1) }
                    .keyboardShortcut(.defaultAction)
                    .disabled(step == .you && draft.userName.trimmingCharacters(in: .whitespaces).isEmpty)
            }
        }
        .controlSize(.large)
        .padding(.horizontal, 24)
        .padding(.vertical, 16)
        .background(Color.white.opacity(0.03))
    }

    // MARK: Flow

    private func load() {
        guard !loaded else { return }
        loaded = true
        draft = assistant.config
        // The neutral default is not a name: the field starts empty.
        if draft.userName == "Utente" { draft.userName = "" }
    }

    private func move(_ delta: Int) {
        if delta > 0, step != .welcome { save() }
        step = Step(rawValue: step.rawValue + delta) ?? step
    }

    /// Each step is saved when you go on: closing halfway keeps what was already written.
    private func save(finished: Bool = false) {
        var config = draft
        let name = config.userName.trimmingCharacters(in: .whitespaces)
        config.userName = name.isEmpty ? "Utente" : name
        // Whisper must write the user's name correctly.
        // Every single word already there counts ("Mario Rossi" already holds "Mario").
        var words = Set(config.vocabulary.components(separatedBy: CharacterSet(charactersIn: ", ."))
            .filter { !$0.isEmpty }.map { $0.lowercased() })
        for word in [name] + config.userFullName.split(separator: " ").map(String.init)
        where word.count > 1 && !words.contains(word.lowercased()) {
            words.insert(word.lowercased())
            config.vocabulary = config.vocabulary.trimmingCharacters(in: .whitespaces.union(CharacterSet(charactersIn: ".")))
                + ", \(word)."
        }
        draft.vocabulary = config.vocabulary
        assistant.applySetup(config, finished: finished)
    }

    private func finish() {
        save(finished: true)
        onClose()
    }
}

// MARK: - Pieces

/// Title and explanation at the top of each step.
struct StepIntro: View {
    let title: String
    let text: String

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title).font(.system(size: 22, weight: .semibold))
            Text(text).font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding(.horizontal, 24)
        .padding(.top, 14)
    }
}

struct Caption: View {
    let text: String
    init(_ text: String) { self.text = text }

    var body: some View {
        Text(text).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
    }
}

struct StatusLine: View {
    let ok: Bool
    let text: String

    var body: some View {
        Label(text, systemImage: ok ? "checkmark.circle.fill" : "exclamationmark.circle")
            .foregroundStyle(ok ? Color.green : HUD.amber)
            .font(.callout)
    }
}

// MARK: - Steps

private struct WelcomeStep: View {
    var body: some View {
        VStack(spacing: 18) {
            Spacer(minLength: 10)
            ZStack {
                Circle().stroke(HUD.cyan.opacity(0.25), lineWidth: 10).frame(width: 110, height: 110)
                Circle().stroke(HUD.cyan, lineWidth: 2).frame(width: 84, height: 84)
                    .shadow(color: HUD.cyan, radius: 10)
                Text("AL").font(.system(size: 30, weight: .bold, design: .monospaced)).foregroundStyle(HUD.ice)
            }
            Text("Ciao, sono AL-76").font(.system(size: 28, weight: .semibold))
            Text("Un assistente vocale che vive sul tuo Mac. Il nome viene da un racconto di Isaac Asimov: un robot perso sulla Terra che l'uomo che lo trova chiama semplicemente «Al». Puoi chiamarmi così anche tu: «Ehi Al».")
                .multilineTextAlignment(.center)
                .foregroundStyle(.secondary)
                .frame(maxWidth: 520)
            VStack(alignment: .leading, spacing: 8) {
                Label("Rispondo a voce o per scritto (⌃⌥T), apro app e file, leggo posta e calendario", systemImage: "waveform")
                Label("Ti aiuto a studiare: piano, esami, appunti, flashcard", systemImage: "book")
                Label("Il riconoscimento della voce e il cervello locale restano sul Mac", systemImage: "lock")
            }
            .font(.callout)
            .padding(16)
            .background(Color.white.opacity(0.05), in: RoundedRectangle(cornerRadius: 12))
            Text("In pochi passi ti preparo tutto. Ogni scelta si cambia poi nelle Impostazioni.")
                .font(.caption).foregroundStyle(.secondary)
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 40)
    }
}

private struct YouStep: View {
    @Binding var draft: JarvisConfig
    private static let titles = ["", "signore", "signora"]

    var body: some View {
        VStack(spacing: 0) {
            StepIntro(title: "Chi sei", text: "Quello che scrivi qui entra nelle mie istruzioni, così le risposte sono su misura per te.")
            Form {
                Section("Come ti chiami") {
                    TextField("Nome", text: $draft.userName)
                    TextField("Nome e cognome (facoltativo)", text: $draft.userFullName)
                }
                Section("Come ti chiamo") {
                    Picker("Titolo", selection: $draft.honorific) {
                        Text("Nessun titolo").tag("")
                        Text("signore").tag("signore")
                        Text("signora").tag("signora")
                        if !Self.titles.contains(draft.honorific) { Text(draft.honorific).tag(draft.honorific) }
                    }
                    TextField("Oppure scrivine uno tuo (es. capo, prof)", text: $draft.honorific)
                    Caption("Senza titolo non ti chiamo né «signore» né per nome: rispondo e basta.")
                }
                Section("Di te") {
                    TextField("Una riga su di te: es. studente di ingegneria robotica, lavoro come tecnico", text: $draft.userAbout, axis: .vertical)
                        .lineLimit(2...4)
                    TextField("Interessi: es. robotica, stampa 3D, astronomia", text: $draft.userInterests, axis: .vertical)
                        .lineLimit(1...3)
                    Caption("Restano su questo Mac. Se scegli Claude come cervello, arrivano ad Anthropic insieme alle domande.")
                }
            }
            .formStyle(.grouped)
            .scrollContentBackground(.hidden)
        }
    }
}

private struct StudyStep: View {
    let assistant: Assistant
    @Binding var draft: JarvisConfig
    @State private var subjects: [StudyPlan.Subject] = []
    @State private var name = ""
    @State private var hasExam = false
    @State private var exam = Calendar.current.date(byAdding: .month, value: 1, to: Date()) ?? Date()
    @State private var hours = 6.0

    var body: some View {
        VStack(spacing: 0) {
            StepIntro(title: "Cosa studi", text: "Con materie ed esami ti dico cosa conviene studiare oggi e la sera ti faccio il riepilogo. Tutto facoltativo: puoi aggiungerli anche a voce («Ehi Al, l'esame di Fisica è il 20 gennaio»).")
            Form {
                Section("Materie ed esami") {
                    ForEach(subjects, id: \.name) { subject in
                        HStack {
                            VStack(alignment: .leading) {
                                Text(subject.name)
                                Caption(describe(subject))
                            }
                            Spacer()
                            Button { assistant.removeStudySubject(subject.name); reload() } label: { Image(systemName: "minus.circle") }
                                .buttonStyle(.borderless)
                        }
                    }
                    TextField("Materia", text: $name)
                    Toggle("Data dell'esame", isOn: $hasExam)
                    if hasExam { DatePicker("Esame", selection: $exam, displayedComponents: .date) }
                    Stepper(hours == 0 ? "Nessun obiettivo di ore" : "\(Int(hours)) ore a settimana", value: $hours, in: 0...40)
                    Button("Aggiungi materia") {
                        assistant.setStudySubject(name, exam: hasExam ? exam : nil, weeklyHours: hours)
                        name = ""; hasExam = false
                        reload()
                    }
                    .disabled(name.trimmingCharacters(in: .whitespaces).isEmpty)
                }
                Section("Cartelle degli appunti") {
                    ForEach(draft.notesFolders, id: \.self) { folder in
                        HStack {
                            Text(folder).lineLimit(1).truncationMode(.middle)
                            Spacer()
                            Button { draft.notesFolders.removeAll { $0 == folder } } label: { Image(systemName: "minus.circle") }
                                .buttonStyle(.borderless)
                        }
                    }
                    Button("Aggiungi cartella…", action: addFolder)
                    Caption("Cerco dentro PDF, Word, PowerPoint e testi solo di queste cartelle, tutto sul Mac. Il resto, come i documenti personali, resta fuori.")
                }
                UniversitySection(assistant: assistant, draft: $draft)
            }
            .formStyle(.grouped)
            .scrollContentBackground(.hidden)
        }
        .onAppear(perform: reload)
    }

    private func reload() { subjects = assistant.studySubjects }

    private func describe(_ subject: StudyPlan.Subject) -> String {
        var parts: [String] = []
        if let exam = subject.exam { parts.append("esame " + exam.formatted(.dateTime.day().month(.wide).year())) }
        if let minutes = subject.weeklyMinutes { parts.append("\(minutes / 60) ore a settimana") }
        return parts.isEmpty ? "senza data né ore" : parts.joined(separator: " · ")
    }

    private func addFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.allowsMultipleSelection = true
        panel.prompt = "Aggiungi"
        guard panel.runModal() == .OK else { return }
        let home = NSHomeDirectory()
        for url in panel.urls {
            let path = url.path.hasPrefix(home) ? "~" + url.path.dropFirst(home.count) : url.path
            if !draft.notesFolders.contains(path) { draft.notesFolders.append(path) }
        }
    }
}

private struct BrainStep: View {
    @Binding var draft: JarvisConfig
    @State private var ollama: String?
    @State private var ollamaOK = false

    var body: some View {
        VStack(spacing: 0) {
            StepIntro(title: "Il cervello", text: "Le cose semplici (ore, timer, app, musica) le faccio da solo all'istante. Per le domande vere uso un modello: uno locale, gratis e privato, oppure uno più potente con un abbonamento che hai già, senza pagare API.")
            Form {
                Section("Cervello locale") {
                    StatusLine(ok: ollamaOK, text: ollama ?? "Controllo Ollama…")
                    Caption("Gira sul Mac con Ollama: le domande non escono dal computer. Serve circa 7 GB di spazio e almeno 16 GB di RAM.")
                }
                Section("Cervello in uso") {
                    BrainPicker(selection: $draft.brain)
                }
            }
            .formStyle(.grouped)
            .scrollContentBackground(.hidden)
        }
        .task { await checkOllama() }
    }

    private func checkOllama() async {
        struct Tags: Decodable { struct Model: Decodable { let name: String }; let models: [Model] }
        guard let url = URL(string: draft.ollamaURL + "/api/tags"),
              let (data, _) = try? await URLSession.shared.data(from: url),
              let tags = try? JSONDecoder().decode(Tags.self, from: data) else {
            ollama = "Ollama non risponde: installalo con scripts/setup.sh"
            return
        }
        ollamaOK = tags.models.contains { $0.name == draft.localModel }
        ollama = ollamaOK ? "Ollama attivo, modello \(draft.localModel) pronto" : "Ollama attivo, ma manca il modello: «ollama pull \(draft.localModel)»"
    }
}

/// The macOS permissions, one per row, each with what it is for. They can all be given later too:
/// macOS asks each one the first time it is needed.
enum Permission: CaseIterable, Identifiable {
    case microphone, accessibility, calendar, reminders, contacts, screen, camera

    var id: Self { self }

    var name: String {
        switch self {
        case .microphone: return "Microfono"
        case .accessibility: return "Accessibilità"
        case .calendar: return "Calendario"
        case .reminders: return "Promemoria"
        case .contacts: return "Contatti"
        case .screen: return "Registrazione schermo"
        case .camera: return "Fotocamera"
        }
    }

    var icon: String {
        switch self {
        case .microphone: return "mic"
        case .accessibility: return "hand.tap"
        case .calendar: return "calendar"
        case .reminders: return "checklist"
        case .contacts: return "person.crop.circle"
        case .screen: return "rectangle.dashed"
        case .camera: return "camera"
        }
    }

    var why: String {
        switch self {
        case .microphone: return "Per sentire «Ehi Al» e i comandi. Indispensabile per la voce; l'audio resta sul Mac."
        case .accessibility: return "Per premere pulsanti e scrivere nelle altre app (es. un messaggio su WhatsApp). Mai in password, banca e Impostazioni."
        case .calendar: return "Per leggere e creare appuntamenti e avvisarti prima."
        case .reminders: return "Per leggere e creare promemoria."
        case .contacts: return "Per trovare l'indirizzo delle persone a cui vuoi scrivere."
        case .screen: return "Per «guarda il mio schermo» e per mostrare lo schermo sull'iPhone. Vale dal prossimo avvio di AL-76."
        case .camera: return "Per «guardami» e «cosa ho in mano»: una foto dalla fotocamera, solo quando lo chiedi."
        }
    }

    var settingsAnchor: String {
        switch self {
        case .microphone: return "Privacy_Microphone"
        case .accessibility: return "Privacy_Accessibility"
        case .calendar: return "Privacy_Calendars"
        case .reminders: return "Privacy_Reminders"
        case .contacts: return "Privacy_Contacts"
        case .screen: return "Privacy_ScreenCapture"
        case .camera: return "Privacy_Camera"
        }
    }

    var granted: Bool {
        switch self {
        case .microphone: return AVCaptureDevice.authorizationStatus(for: .audio) == .authorized
        case .accessibility: return AXIsProcessTrusted()
        case .calendar: return EKEventStore.authorizationStatus(for: .event) == .fullAccess
        case .reminders: return EKEventStore.authorizationStatus(for: .reminder) == .fullAccess
        case .contacts: return CNContactStore.authorizationStatus(for: .contacts) == .authorized
        case .screen: return CGPreflightScreenCaptureAccess()
        case .camera: return AVCaptureDevice.authorizationStatus(for: .video) == .authorized
        }
    }

    /// macOS shows its dialog only the first time: after a "no" the switch is in System Settings.
    var undecided: Bool {
        switch self {
        case .microphone: return AVCaptureDevice.authorizationStatus(for: .audio) == .notDetermined
        case .calendar: return EKEventStore.authorizationStatus(for: .event) == .notDetermined
        case .reminders: return EKEventStore.authorizationStatus(for: .reminder) == .notDetermined
        case .contacts: return CNContactStore.authorizationStatus(for: .contacts) == .notDetermined
        case .camera: return AVCaptureDevice.authorizationStatus(for: .video) == .notDetermined
        case .accessibility, .screen: return true
        }
    }

    @MainActor
    func request() async {
        guard undecided else { return openSettings() }
        switch self {
        case .microphone: _ = await AVCaptureDevice.requestAccess(for: .audio)
        case .accessibility:
            let options = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
            if !AXIsProcessTrustedWithOptions(options) { openSettings() }
        case .calendar: _ = try? await EKEventStore().requestFullAccessToEvents()
        case .reminders: _ = try? await EKEventStore().requestFullAccessToReminders()
        case .contacts: _ = try? await CNContactStore().requestAccess(for: .contacts)
        case .screen: if !CGRequestScreenCaptureAccess() { openSettings() }
        case .camera: _ = await AVCaptureDevice.requestAccess(for: .video)
        }
    }

    func openSettings() {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(settingsAnchor)") {
            NSWorkspace.shared.open(url)
        }
    }
}

private struct PermissionsStep: View {
    @State private var granted: Set<Permission> = []
    private let timer = Timer.publish(every: 1, on: .main, in: .common).autoconnect()

    var body: some View {
        VStack(spacing: 0) {
            StepIntro(title: "I permessi", text: "macOS mi lascia fare solo ciò che gli permetti tu. Ecco a cosa serve ognuno: dai quelli che vuoi, gli altri te li chiederò la prima volta che servono. Si tolgono in Impostazioni di Sistema › Privacy e sicurezza.")
            Form {
                Section {
                    ForEach(Permission.allCases) { permission in
                        HStack(alignment: .top, spacing: 12) {
                            Image(systemName: permission.icon)
                                .font(.title3)
                                .foregroundStyle(HUD.cyan)
                                .frame(width: 26)
                            VStack(alignment: .leading, spacing: 2) {
                                Text(permission.name).font(.body.weight(.medium))
                                Caption(permission.why)
                            }
                            Spacer()
                            if granted.contains(permission) {
                                Image(systemName: "checkmark.circle.fill").foregroundStyle(.green).font(.title3)
                            } else {
                                Button(permission.undecided ? "Consenti" : "Apri Impostazioni") {
                                    Task { await permission.request(); refresh() }
                                }
                            }
                        }
                        .padding(.vertical, 2)
                    }
                }
                Section {
                    Caption("Mail, Musica, Finder e le altre app: macOS chiede il permesso di controllarle la prima volta che me lo chiedi. L'accesso completo al disco è facoltativo: rende più veloce la ricerca nella posta.")
                }
            }
            .formStyle(.grouped)
            .scrollContentBackground(.hidden)
        }
        .onAppear(perform: refresh)
        .onReceive(timer) { _ in refresh() }
    }

    private func refresh() {
        granted = Set(Permission.allCases.filter(\.granted))
    }
}

private struct VoiceStep: View {
    @ObservedObject var assistant: Assistant
    @ObservedObject var voicePrint: VoicePrint
    @State private var heardCall = false
    @State private var enrolling = false

    private var micGranted: Bool { AVCaptureDevice.authorizationStatus(for: .audio) == .authorized }

    private var bootText: String? {
        if case .booting(let text) = assistant.state { return text }
        return nil
    }

    var body: some View {
        VStack(spacing: 0) {
            StepIntro(title: "La tua voce", text: "Mi chiami con «Ehi Al», poi dici cosa ti serve. Oppure tieni premuto ⌃⌥Spazio, o scrivi con ⌃⌥T.")
            Form {
                Section("1 · Ascolto") {
                    if !micGranted {
                        StatusLine(ok: false, text: "Manca il permesso del microfono: torna ai Permessi.")
                    } else if let bootText {
                        HStack(spacing: 8) {
                            ProgressView().controlSize(.small)
                            Text("Preparo il riconoscimento vocale: \(bootText)").font(.callout)
                        }
                        Caption("La prima volta scarico Whisper (circa 800 MB) e lo preparo per il Neural Engine: qualche minuto.")
                    } else {
                        StatusLine(ok: true, text: "Ti sto ascoltando")
                    }
                }
                Section("2 · Prova la chiamata") {
                    if heardCall {
                        StatusLine(ok: true, text: "Ti ho sentito!")
                    } else {
                        Text("Di' «Ehi Al» a voce normale.").font(.title3)
                    }
                    Caption("Dopo il beep puoi anche chiedermi qualcosa, per esempio «che ore sono?».")
                }
                Section("3 · Impronta della voce (consigliata)") {
                    LabeledContent("Impronta", value: voicePrint.isEnrolled ? "\(voicePrint.samples.count) frasi registrate" : "non registrata")
                    Button(voicePrint.isEnrolled ? "Registra di nuovo" : "Registra la mia voce (2 minuti)") {
                        assistant.startVoiceEnrollment()
                        enrolling = true
                    }
                    .disabled(bootText != nil || !micGranted)
                    Caption("Leggi alcune frasi con «Ehi Al»: così riconosco te e non la TV o altre persone. Resta su questo Mac e non conservo l'audio.")
                }
            }
            .formStyle(.grouped)
            .scrollContentBackground(.hidden)
        }
        .onChange(of: assistant.state) { _, state in
            if state == .listening { heardCall = true }
        }
        .sheet(isPresented: $enrolling) {
            VoiceEnrollmentView(enrollment: assistant.voiceEnrollment)
        }
    }
}

private struct ExtrasStep: View {
    @Binding var draft: JarvisConfig
    @State private var launchAtLogin = SMAppService.mainApp.status == .enabled

    var body: some View {
        VStack(spacing: 0) {
            StepIntro(title: "Ultimi ritocchi", text: "Come mi vedi sullo schermo, il telefono e le abitudini di tutti i giorni.")
            Form {
                Section("Aspetto") {
                    Picker("Sullo schermo", selection: $draft.nucleusStyle) {
                        Text("Reattore").tag("reattore")
                        Text("Satellite").tag("satellite")
                        if RobotNucleusView.modelAvailable { Text("Robottino").tag("robottino") }
                    }
                    .pickerStyle(.segmented)
                    Caption("Resto in un angolo e mi animo quando ascolto e parlo. Doppio clic per aprire l'HUD completo.")
                }
                Section("iPhone e iPad") {
                    Toggle("Collegamento con l'app per iPhone", isOn: $draft.phoneBridge)
                    Caption("Mi parli dal telefono, in casa o fuori con Tailscale. Il QR per abbinarlo è in Impostazioni › iPhone; l'app si installa come spiega INSTALL.md.")
                }
                Section("Ogni giorno") {
                    Toggle("Briefing alla prima chiamata del mattino", isOn: $draft.morningBriefing)
                    Toggle("Saluto all'avvio", isOn: $draft.greetOnLaunch)
                    Toggle("Avvia AL-76 al login", isOn: $launchAtLogin)
                        .onChange(of: launchAtLogin) { _, enabled in
                            try? enabled ? SMAppService.mainApp.register() : SMAppService.mainApp.unregister()
                        }
                }
                Section("Stampante 3D, TV, robot") {
                    Caption("Se li hai, si collegano dopo: la stampante Bambu in Impostazioni, il resto a voce o dalle schede.")
                }
            }
            .formStyle(.grouped)
            .scrollContentBackground(.hidden)
        }
    }
}

private struct DoneStep: View {
    @ObservedObject var assistant: Assistant
    @ObservedObject var voicePrint: VoicePrint
    let draft: JarvisConfig

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            StepIntro(title: "Pronto", text: "Ecco cosa ho imparato. Tutto si cambia in Impostazioni (⚙︎ nell'HUD), e questa configurazione si riapre dal menu di AL-76.")
            VStack(alignment: .leading, spacing: 8) {
                let name = draft.userName.trimmingCharacters(in: .whitespaces)
                StatusLine(ok: !name.isEmpty, text: name.isEmpty ? "Nome non scritto" : "Ti chiami \(name)")
                StatusLine(ok: !assistant.studySubjects.isEmpty, text: assistant.studySubjects.isEmpty ? "Nessuna materia (si aggiungono anche a voce)" : "\(assistant.studySubjects.count) materie nel piano di studio")
                StatusLine(ok: !draft.notesFolders.isEmpty, text: "\(draft.notesFolders.count) cartelle di appunti")
                StatusLine(ok: true, text: "Cervello: " + (Assistant.Brain(rawValue: draft.brain).map { $0 == .local ? "locale" : $0.title } ?? "locale"))
                let given = Permission.allCases.filter(\.granted).count
                StatusLine(ok: Permission.microphone.granted, text: "\(given) permessi su \(Permission.allCases.count)")
                StatusLine(ok: voicePrint.isEnrolled, text: voicePrint.isEnrolled ? "Impronta della voce registrata" : "Impronta della voce non registrata")
            }
            .padding(.horizontal, 24)
            VStack(alignment: .leading, spacing: 6) {
                Text("Prova a dire").font(.headline)
                Text("«Ehi Al, che ore sono?»")
                Text("«Ehi Al, cosa devo studiare oggi?»")
                Text("«Ehi Al, apri le dispense di …»")
            }
            .foregroundStyle(HUD.ice)
            .padding(16)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(Color.white.opacity(0.05), in: RoundedRectangle(cornerRadius: 12))
            .padding(.horizontal, 24)
        }
    }
}

// MARK: - Brain choice

/// The four brains, each with whether it is installed and signed in, and what to type when it is not.
/// Shared by the first-run setup and Settings › Cervello.
struct BrainPicker: View {
    @Binding var selection: String
    @State private var statuses: [String: String] = [:]
    @State private var geminiTools = GeminiBridge.toolsAllowed

    private struct Option {
        let id: String
        let title: String
        let detail: String
        let setup: String
        let bridge: CloudBridge?
    }

    private static let options = [
        Option(id: "local", title: "Solo il nucleo locale", detail: "Gemma con Ollama, sul Mac: gratis e privato.", setup: "", bridge: nil),
        Option(id: "claude", title: "Claude", detail: "Abbonamento Claude, tramite Claude Code.",
               setup: "brew install --cask claude-code, poi claude auth login --claudeai", bridge: ClaudeBridge()),
        Option(id: "chatgpt", title: "ChatGPT", detail: "Abbonamento ChatGPT, tramite Codex di OpenAI.",
               setup: "brew install --cask codex, poi codex login", bridge: CodexBridge()),
        Option(id: "gemini", title: "Gemini", detail: "Account Google (Gemini gratuito o abbonamento), tramite Antigravity CLI.",
               setup: "curl -fsSL https://antigravity.google/cli/install.sh | bash, poi agy (accedi con Google)", bridge: GeminiBridge()),
    ]

    var body: some View {
        ForEach(Self.options, id: \.id) { option in
            let status = option.bridge == nil ? "COLLEGATO" : statuses[option.id]
            let usable = option.bridge == nil || (status != nil && status != "NON INSTALLATO")
            Button { if usable { selection = option.id } } label: {
                HStack(alignment: .top, spacing: 10) {
                    Image(systemName: selection == option.id ? "largecircle.fill.circle" : "circle")
                        .foregroundStyle(selection == option.id ? HUD.cyan : .secondary)
                        .font(.title3)
                    VStack(alignment: .leading, spacing: 3) {
                        Text(option.title).font(.body.weight(.medium))
                        Caption(option.detail)
                        if option.bridge != nil {
                            if let status {
                                StatusLine(ok: status == "COLLEGATO", text: Self.describe(status))
                                if status != "COLLEGATO" {
                                    Text(option.setup).font(.system(.caption, design: .monospaced)).foregroundStyle(HUD.ice)
                                        .textSelection(.enabled)
                                }
                                if option.id == "gemini", status == "COLLEGATO", !geminiTools {
                                    Caption("Da solo Antigravity esegue comandi e scrive file. Questo pulsante aggiunge alle sue impostazioni le regole che gli permettono solo gli strumenti di AL-76: niente terminale, niente modifiche, niente pagine web (valgono anche se usi agy per conto tuo).")
                                    Button("Prepara Antigravity per AL-76") {
                                        try? GeminiBridge.allowTools()
                                        geminiTools = GeminiBridge.toolsAllowed
                                    }
                                }
                            } else {
                                Caption("Controllo…")
                            }
                        }
                    }
                    Spacer()
                }
                .contentShape(Rectangle())
                .opacity(usable ? 1 : 0.75)
            }
            .buttonStyle(.plain)
        }
        .task { await check() }
        Caption("Con un abbonamento, le domande e ciò che serve per rispondere vanno al fornitore scelto, senza pagare API. Si cambia anche a voce: «Ehi Al, usa ChatGPT», «passa a Gemini», «torna al nucleo locale».")
    }

    private static func describe(_ status: String) -> String {
        switch status {
        case "COLLEGATO": return "Installato e collegato"
        case "LOGIN NECESSARIO": return "Installato, manca l'accesso"
        default: return "Non installato"
        }
    }

    private func check() async {
        for option in Self.options {
            if let bridge = option.bridge { statuses[option.id] = await bridge.status() }
        }
    }
}
