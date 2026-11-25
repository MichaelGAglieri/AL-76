import AVFoundation
import CoreImage
import ServiceManagement
import SwiftUI

struct SettingsView: View {
    @ObservedObject var assistant: Assistant
    @State private var draft = JarvisConfig()
    @State private var launchAtLogin = SMAppService.mainApp.status == .enabled

    private var italianVoices: [String] { Self.voiceNames("it-IT") }
    private var englishVoices: [String] { Self.voiceNames("en-GB") }

    /// The pages of the window, in the style of the first-run setup (v1.30.67, user request).
    enum Page: String, CaseIterable, Identifiable {
        case profile, activation, voice, brain, study, permissions, apps, initiative, look, phone, printer, system

        var id: Self { self }

        var title: String {
            switch self {
            case .profile: return "Profilo"
            case .activation: return "Attivazione"
            case .voice: return "Voce"
            case .brain: return "Cervello"
            case .study: return "Studio"
            case .permissions: return "Permessi"
            case .apps: return "Controllo delle app"
            case .initiative: return "Iniziativa"
            case .look: return "Aspetto"
            case .phone: return "iPhone"
            case .printer: return "Stampante"
            case .system: return "Sistema"
            }
        }

        var icon: String {
            switch self {
            case .profile: return "person.crop.circle"
            case .activation: return "waveform"
            case .voice: return "speaker.wave.2"
            case .brain: return "brain"
            case .study: return "book"
            case .permissions: return "lock.shield"
            case .apps: return "hand.tap"
            case .initiative: return "bell"
            case .look: return "sparkles"
            case .phone: return "iphone"
            case .printer: return "printer"
            case .system: return "gearshape"
            }
        }

        var intro: String {
            switch self {
            case .profile: return "Chi sei e come ti chiamo. Entra nelle mie istruzioni, così le risposte sono su misura per te."
            case .activation: return "Come mi chiami e come mi interrompi."
            case .voice: return "La tua impronta vocale e la voce con cui ti rispondo."
            case .brain: return "Chi risponde alle domande vere: il modello locale, oppure Claude, ChatGPT o Gemini con un abbonamento che hai già."
            case .study: return "Le cartelle in cui cerco gli appunti e l'app della tua università."
            case .permissions: return "Tutto ciò che posso fare, uno per uno: i permessi di macOS e quelli miei. Spegni quello che non vuoi che faccia."
            case .apps: return "Pulsanti e campi delle altre app, e il Touch ID per le azioni delicate."
            case .initiative: return "Quando parlo per primo, e quando sto zitto."
            case .look: return "Come mi vedi sullo schermo."
            case .phone: return "Il collegamento con l'app per iPhone e iPad."
            case .printer: return "Lo stato della stampante Bambu sulla rete di casa."
            case .system: return "Avvio e saluto."
            }
        }
    }

    @State private var page = Page.profile

    var body: some View {
        VStack(spacing: 0) {
            HStack(spacing: 0) {
                sidebar
                Divider().opacity(0.4)
                VStack(spacing: 0) {
                    StepIntro(title: page.title, text: page.intro)
                    Form { content }
                        .formStyle(.grouped)
                        .scrollContentBackground(.hidden)
                }
                .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .top)
            }
            footer
        }
        .frame(width: 800, height: 620)
        .background(HUD.background.ignoresSafeArea())
        .toolbarBackground(.hidden, for: .windowToolbar)
        .toolbar(removing: .title)
        .tint(HUD.cyan)
        .preferredColorScheme(.dark)
        .background(HUDWindowStyle())
        .onAppear { draft = assistant.config }
    }

    private var sidebar: some View {
        VStack(alignment: .leading, spacing: 2) {
            ForEach(Page.allCases) { item in
                Button { page = item } label: {
                    Label(item.title, systemImage: item.icon)
                        .font(.system(size: 13, weight: item == page ? .semibold : .regular))
                        .foregroundStyle(item == page ? HUD.cyan : .primary)
                        .frame(maxWidth: .infinity, alignment: .leading)
                        .padding(.horizontal, 10)
                        .padding(.vertical, 7)
                        .background(item == page ? HUD.cyan.opacity(0.12) : .clear, in: RoundedRectangle(cornerRadius: 8))
                        .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
            Spacer()
        }
        .padding(.horizontal, 10)
        .padding(.top, 14)
        .frame(width: 200)
        .background(Color.white.opacity(0.03))
    }

    private var footer: some View {
        HStack {
            if draft != assistant.config {
                Text("Modifiche non salvate").font(.caption).foregroundStyle(HUD.amber)
                Button("Annulla") { draft = assistant.config }
            }
            Spacer()
            Button("Salva") { assistant.apply(draft) }
                .keyboardShortcut(.defaultAction)
                .disabled(draft == assistant.config)
        }
        .controlSize(.large)
        .padding(.horizontal, 24)
        .padding(.vertical, 14)
        .background(Color.white.opacity(0.03))
    }

    @ViewBuilder private var content: some View {
        switch page {
        case .profile:
            Section("Come ti chiami") {
                TextField("Nome", text: $draft.userName)
                TextField("Nome e cognome", text: $draft.userFullName)
            }
            Section("Come ti chiamo") {
                Picker("Titolo", selection: $draft.honorific) {
                    Text("Nessun titolo").tag("")
                    Text("signore").tag("signore")
                    Text("signora").tag("signora")
                    if !["", "signore", "signora"].contains(draft.honorific) { Text(draft.honorific).tag(draft.honorific) }
                }
                TextField("Oppure scrivine uno tuo (es. capo, prof)", text: $draft.honorific)
            }
            Section("Di te") {
                TextField("Una riga su di te", text: $draft.userAbout, axis: .vertical).lineLimit(2...5)
                TextField("Interessi", text: $draft.userInterests, axis: .vertical).lineLimit(1...3)
                TextField("Parole che Whisper deve scrivere giuste", text: $draft.vocabulary, axis: .vertical).lineLimit(2...5)
                Caption("Nomi, sigle e termini tecnici, separati da virgole: li suggerisco a Whisper prima di ogni trascrizione.")
            }
        case .activation:
            Section("Chiamata") {
                Toggle("Ascolta sempre \"Ehi Al\" (Whisper, gratuito)", isOn: $draft.whisperWake)
                Caption("Se lo spegni AL-76 si attiva solo con ⌃⌥Spazio, e consuma ancora meno.")
                Toggle("Continua la conversazione senza ripetere \"Ehi Al\"", isOn: $draft.followUpConversation)
                Toggle("Interrompilo a voce (\"basta\")", isOn: $draft.bargeIn)
                Toggle("Cancellazione dell'eco (serve con gli altoparlanti)", isOn: $draft.echoCancellation)
                Caption("La cancellazione dell'eco vale dal riavvio di AL-76.")
            }
            Section("Porcupine (facoltativo)") {
                SecureField("Picovoice AccessKey", text: $draft.picovoiceAccessKey)
                Slider(value: $draft.wakeSensitivity, in: 0.3...0.9, step: 0.05) {
                    Text("Sensibilità \(draft.wakeSensitivity, format: .number.precision(.fractionLength(2)))")
                }
                Caption("Con una chiave Picovoice valida si usa Porcupine al posto di Whisper. Porcupine conosce ancora solo la parola «Jarvis»: per «Ehi Al» va addestrata una parola nuova su Picovoice.")
            }
        case .voice:
            VoicePrintSection(assistant: assistant, voicePrint: assistant.voicePrint, draft: $draft)
            Section("Voce di AL-76") {
                Toggle("Voce neurale (Qwen3 in italiano, Lewis in inglese)", isOn: $draft.neuralVoice)
                if draft.neuralVoice {
                    Slider(value: $draft.kokoroSpeed, in: 0.8...1.2) { Text("Velocità voce inglese") }
                }
                Caption("Le voci Apple qui sotto si usano se la voce neurale è spenta o non disponibile.")
                Picker("Voce italiana", selection: $draft.italianVoice) {
                    ForEach(italianVoices, id: \.self) { Text($0).tag($0) }
                }
                Picker("Voce inglese", selection: $draft.englishVoice) {
                    ForEach(englishVoices, id: \.self) { Text($0).tag($0) }
                }
                Slider(value: $draft.speechRate, in: 0.4...0.62) { Text("Velocità") }
                Caption("Altre voci: Impostazioni di Sistema › Accessibilità › Contenuti letti ad alta voce › Gestisci voci.")
            }
        case .brain:
            Section("Intelligenza artificiale") {
                // Applied at once, like the brain picker; the draft follows so that «Salva» does not undo it.
                Toggle("Senza IA: solo i comandi che conosco", isOn: Binding(
                    get: { assistant.config.noAI },
                    set: { off in
                        assistant.setArtificialIntelligence(!off)
                        draft.noAI = off
                    }))
                Caption("Spenta l'IA, AL-76 non usa nessun modello, né Claude, ChatGPT o Gemini né Gemma sul Mac: fa solo i comandi che conosce (app, volume, timer, sessioni, mail, calendario, promemoria, meteo, musica, cartelle, stampante, TV…). Una richiesta composta («apri Safari e metti un timer») la divide e fa le parti che conosce; per il resto dice che non può e suggerisce i comandi più vicini. Udito, voce e impronta vocale restano. A voce: «disattiva l'IA» · «riattiva l'IA». Le richieste rimaste senza risposta si vedono nella Plancia › Stato.")
            }
            Section("Cervello in uso") {
                BrainPicker(selection: Binding(get: { assistant.brain.rawValue },
                                               set: { assistant.setBrain(Assistant.Brain(rawValue: $0) ?? .local) }))
                    .disabled(assistant.config.noAI)
                    .opacity(assistant.config.noAI ? 0.45 : 1)
            }
            Section("Cervello locale") {
                TextField("Modello locale (Ollama)", text: $draft.localModel)
                TextField("Indirizzo Ollama", text: $draft.ollamaURL)
            }
            LearnedPhrasesSection()
        case .study:
            Section("Appunti") {
                TextField("Cartelle, una per riga", text: Binding(
                    get: { draft.notesFolders.joined(separator: "\n") },
                    set: { draft.notesFolders = $0.components(separatedBy: "\n") }
                ), axis: .vertical)
                Caption("AL-76 cerca dentro PDF, Word, PowerPoint e testi di queste cartelle, tutto sul Mac. Le altre cartelle, come i documenti personali, restano fuori. Le nuove cartelle entrano alla prossima lettura, entro 6 ore o al riavvio.")
            }
            UniversitySection(assistant: assistant, draft: $draft)
        case .permissions:
            PermissionsPage(draft: $draft)
        case .apps:
            Section("Controllo delle app") {
                Toggle("Lascia che AL-76 usi pulsanti e campi delle app", isOn: $draft.appControl)
                Toggle("Touch ID per le azioni molto delicate", isOn: $draft.ownerCheck)
                Caption("Script e programmi, comandi rapidi, cancellazione dei ricordi: dopo il \"sì\" serve il dito (o la password del Mac).")
                Caption("Solo con Claude, ChatGPT o Gemini e fuori dalla modalità privacy. Mai password, banca, Impostazioni di Sistema, terminali e editor. Serve il permesso Accessibilità.")
            }
        case .initiative:
            Section("AL-76 parla per primo") {
                Toggle("Appuntamenti: avviso 10 minuti prima", isOn: $draft.alertCalendar)
                Toggle("Promemoria: annuncio alla scadenza", isOn: $draft.alertReminders)
                Toggle("Studio: pausa ogni 2 ore", isOn: $draft.alertStudy)
                Stepper(draft.studySummaryHour == 0 ? "Riepilogo dello studio: spento" : "Riepilogo dello studio alle \(draft.studySummaryHour):00",
                        value: $draft.studySummaryHour, in: 0...22)
                Toggle("Batteria sotto il 20%", isOn: $draft.alertBattery)
                Toggle("Briefing alla prima attivazione del mattino", isOn: $draft.morningBriefing)
            }
            Section("Silenzio") {
                Stepper("Dalle \(draft.quietFrom):00", value: $draft.quietFrom, in: 0...23)
                Stepper("fino alle \(draft.quietTo):00", value: $draft.quietTo, in: 0...23)
                Caption("In privacy, durante una conversazione o con una conferma in sospeso gli avvisi aspettano.")
            }
        case .look:
            Section("SATELLITE") {
                Picker("Aspetto", selection: $draft.nucleusStyle) {
                    Text("Reattore").tag("reattore")
                    Text("Satellite").tag("satellite")
                    Text("Robottino").tag("robottino")
                }
                .pickerStyle(.segmented)
                Toggle("Disco scuro dietro al SATELLITE", isOn: $draft.nucleusBackdrop)
                Caption("Spento si fonde con lo sfondo; acceso resta leggibile su sfondi chiari o sopra le finestre.")
            }
            Section("Icona dell'app") {
                HStack(spacing: 16) {
                    ForEach(AppIconChoice.all, id: \.id) { item in
                        Button { draft.appIcon = item.id } label: {
                            VStack(spacing: 4) {
                                if let icon = AppIconChoice.image(item.id) {
                                    Image(nsImage: icon).resizable().frame(width: 64, height: 64)
                                }
                                Text(item.title).font(.caption)
                            }
                            .padding(6)
                            .background(RoundedRectangle(cornerRadius: 10)
                                .stroke(draft.appIcon == item.id ? Color.accentColor : .clear, lineWidth: 2))
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                    }
                }
                Caption("Nel Dock, nel Finder, in Spotlight e nelle notifiche. Il Dock cambia subito; le notifiche e Spotlight possono metterci qualche istante.")
            }
            if draft.nucleusStyle == "robottino" {
                Section("Robottino") {
                    if RobotNucleusView.modelAvailable {
                        Toggle("Robottino grande", isOn: $draft.robotLarge)
                        Picker("Fiamme dei propulsori", selection: $draft.robotFlames) {
                            Text("Fuoco").tag("fuoco")
                            Text("Ioniche").tag("ioniche")
                        }
                        Toggle("Bloccato nell'angolo in basso a destra", isOn: $draft.robotPinned)
                        Toggle("Passeggia sul bordo inferiore quando aspetta", isOn: $draft.robotWalks)
                            .disabled(draft.robotPinned)
                        Caption("Bloccato non cammina e non si trascina; si blocca e sblocca anche dal suo menu (clic destro) o a voce: «Al, stai fermo» · «Al, puoi camminare». Clic: una carezza · doppio clic: l'HUD · trascina per spostarlo. Le risposte scritte (⌃⌥T) escono dalla sua bocca.")
                    } else {
                        Caption("Manca il modello 3D: metti scene.gltf (con scene.bin e textures) in Application Support/JARVIS/Companion. Intanto si vede il satellite.")
                    }
                }
            }
        case .phone:
            PhoneSection(bridge: assistant.phoneBridge, draft: $draft)
        case .printer:
            Section("Stampante Bambu") {
                TextField("Indirizzo IP", text: $draft.bambuHost)
                TextField("Numero di serie", text: $draft.bambuSerial)
                SecureField("Codice di accesso", text: $draft.bambuCode)
                Caption("Si trovano sul display della stampante, in Rete e Impostazioni. AL-76 legge lo stato: a che punto è, se finisce o si blocca. Caricare, scaricare il filamento e stampare passano dai pulsanti di Bambu Studio, e per stampare chiede prima conferma.")
            }
        case .system:
            Section("Sistema") {
                Toggle("Saluta all'avvio", isOn: $draft.greetOnLaunch)
                Toggle("Avvia AL-76 al login", isOn: $launchAtLogin)
                    .onChange(of: launchAtLogin) { _, enabled in
                        try? enabled ? SMAppService.mainApp.register() : SMAppService.mainApp.unregister()
                    }
                Caption("La configurazione guidata si riapre dal menu di AL-76: «Configurazione guidata…».")
            }
        }
    }

    static func voiceNames(_ language: String) -> [String] {
        let names = AVSpeechSynthesisVoice.speechVoices()
            .filter { $0.language == language }
            .map { $0.name.components(separatedBy: " (").first ?? $0.name }
        return Array(Set(names)).sorted()
    }
}

/// The university: name and server go in config.json with the other settings; username and
/// password go straight to the Keychain, saved by their own button.
struct UniversitySection: View {
    let assistant: Assistant
    @Binding var draft: JarvisConfig
    @State private var username = UniversityClient.savedUsername ?? ""
    @State private var password = ""
    @State private var status = UniversityClient.savedUsername == nil ? "" : "Credenziali salvate nel Portachiavi."
    @State private var testing = false

    var body: some View {
        Section("Università") {
            TextField("Nome dell'università (come la chiama AL-76)", text: $draft.universityName)
            TextField("Indirizzo del server dell'app studenti", text: $draft.universityServer)
            TextField("User-Agent dell'app (se il server lo chiede)", text: $draft.universityAgent)
            TextField("Nome utente (come nell'app dell'università)", text: $username)
                .textContentType(.username)
            SecureField("Password", text: $password)
                .textContentType(.password)
            HStack {
                Button(testing ? "Provo…" : "Salva e prova l'accesso") { saveAndTest() }
                    .disabled(testing || username.trimmingCharacters(in: .whitespaces).isEmpty || password.isEmpty)
                if UniversityClient.savedUsername != nil {
                    Button("Rimuovi", role: .destructive) {
                        UniversityClient.saveCredentials(username: "", password: "")
                        username = ""; password = ""; status = "Credenziali rimosse."
                    }
                }
            }
            if !status.isEmpty { Text(status).font(.caption) }
            Text("Nome utente e password restano solo nel Portachiavi del Mac e vanno solo al server scritto qui sopra, come fa l'app dell'università. AL-76 legge agenda, orario, lezioni ed esiti: non iscrive, non firma, non accetta nulla.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }

    private func saveAndTest() {
        // Name and server are needed for the test: only they are saved now, the other settings wait for "Salva".
        assistant.applyUniversity(from: draft)
        UniversityClient.saveCredentials(username: username, password: password)
        password = ""
        testing = true
        status = "Provo l'accesso…"
        Task {
            do {
                _ = try await UniversityClient.shared.get("user/me")
                status = "Accesso riuscito: AL-76 ora legge i dati dell'università."
                Diagnostics.log("ateneo    credenziali salvate, accesso provato")
            } catch {
                status = error.localizedDescription
                Diagnostics.log("ateneo    prova d'accesso non riuscita: \(error.localizedDescription)")
            }
            testing = false
        }
    }
}

/// Enrolment and use of the voiceprint.
/// Turns on the link and shows how to pair the phone.
private struct PhoneSection: View {
    @ObservedObject var bridge: PhoneBridge
    @Binding var draft: JarvisConfig
    @State private var showCode = false
    @State private var confirmRenew = false

    var body: some View {
        Section("iPhone") {
            Toggle("Collegamento con l'app iPhone", isOn: $draft.phoneBridge)
            Text("Cifrato, solo in rete locale o con Tailscale da fuori casa. Dal telefono si può anche cliccare sullo schermo del Mac e mandare stampe; non si muovono robot e schede, e non si conferma ciò che chiede il Touch ID. Con la corrente attaccata il Mac non va in stop da solo. Vale dopo aver premuto Salva.")
                .font(.caption).foregroundStyle(.secondary)
            if !bridge.problem.isEmpty {
                Text(bridge.problem).font(.caption).foregroundStyle(.orange)
            }
            if draft.phoneBridge {
                Text(bridge.phoneConnected ? "iPhone collegato" : "Nessun iPhone collegato").font(.caption)
                Button(showCode ? "Nascondi il codice" : "Mostra il codice di abbinamento") { showCode.toggle() }
                if showCode {
                    if let image = Self.qrImage(PhoneBridge.pairingLink) {
                        Image(nsImage: image).interpolation(.none).resizable().frame(width: 180, height: 180)
                    }
                    Text(PhoneBridge.pairingCode).font(.system(.body, design: .monospaced)).textSelection(.enabled)
                    Text("Inquadra il QR dall'app, o scrivi il codice.").font(.caption).foregroundStyle(.secondary)
                    if confirmRenew {
                        HStack {
                            Text("Il telefono abbinato andrà riabbinato.").font(.caption)
                            Button("Cambia") {
                                bridge.renewCode()
                                confirmRenew = false
                            }
                            Button("Annulla") { confirmRenew = false }
                        }
                    } else {
                        Button("Cambia codice") { confirmRenew = true }
                    }
                }
            }
        }
    }

    private static func qrImage(_ text: String) -> NSImage? {
        guard let filter = CIFilter(name: "CIQRCodeGenerator") else { return nil }
        filter.setValue(Data(text.utf8), forKey: "inputMessage")
        filter.setValue("M", forKey: "inputCorrectionLevel")
        guard let output = filter.outputImage?.transformed(by: CGAffineTransform(scaleX: 6, y: 6)) else { return nil }
        let rep = NSCIImageRep(ciImage: output)
        let image = NSImage(size: rep.size)
        image.addRepresentation(rep)
        return image
    }
}

private struct VoicePrintSection: View {
    let assistant: Assistant
    @ObservedObject var voicePrint: VoicePrint
    @Binding var draft: JarvisConfig
    @State private var enrolling = false
    @State private var confirmDelete = false

    var body: some View {
        Section("Riconoscimento della mia voce") {
            LabeledContent("Voce", value: voicePrint.isEnrolled ? "\(voicePrint.samples.count) frasi registrate" : "non registrata")
            Button(voicePrint.isEnrolled ? "Registra di nuovo" : "Registra la mia voce (2 minuti)") {
                assistant.startVoiceEnrollment()
                enrolling = true
            }
            if voicePrint.isEnrolled {
                Button("Aggiungi frasi (altre distanze e altri toni)") {
                    assistant.startVoiceEnrollment(adding: true)
                    enrolling = true
                }
                Button("Aggiungi comandi (36 frasi da leggere)") {
                    assistant.startVoiceEnrollment(adding: true, commands: true)
                    enrolling = true
                }
                Picker("Riconoscimento", selection: $draft.voiceCheck) {
                    Text("Spento").tag("off")
                    Text("Solo misura").tag("observe")
                    Text("Attivo").tag("on")
                }
                if draft.voiceCheck == "on" {
                    Slider(value: $draft.voiceThreshold, in: 0.3...0.9, step: 0.01) {
                        Text("Severità \(draft.voiceThreshold, format: .number.precision(.fractionLength(2)))")
                    }
                }
                Button("Cancella l'impronta", role: .destructive) { confirmDelete = true }
            }
            Text("Vale per \"Ehi Al\" e per il comando detto dopo il beep. ⌃⌥Spazio funziona sempre, \"basta\" e il \"sì\" valgono per tutti. L'audio non viene conservato; l'impronta resta su questo Mac. Non è una protezione: le azioni delicate chiedono comunque conferma.")
                .font(.caption).foregroundStyle(.secondary)
        }
        .sheet(isPresented: $enrolling) {
            VoiceEnrollmentView(enrollment: assistant.voiceEnrollment)
        }
        .confirmationDialog("Cancellare l'impronta vocale?", isPresented: $confirmDelete) {
            Button("Cancella", role: .destructive) { assistant.deleteVoicePrint() }
        }
    }
}

struct VoiceEnrollmentView: View {
    @ObservedObject var enrollment: VoiceEnrollment
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        VStack(spacing: 18) {
            Text("Registrazione della voce").font(.headline)
            if enrollment.isActive {
                ProgressView(value: enrollment.progress)
                Text(enrollment.prompt)
                    .font(.title2.weight(.medium))
                    .multilineTextAlignment(.center)
                    .frame(maxWidth: .infinity, minHeight: 80)
                Text("Frase \(enrollment.step + 1) di \(enrollment.prompts.count)")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Text(enrollment.message).foregroundStyle(.secondary).multilineTextAlignment(.center)
            Button(enrollment.isActive ? "Annulla" : "Chiudi") { dismiss() }
                .keyboardShortcut(.cancelAction)
        }
        .padding(24)
        .frame(width: 420)
        .onDisappear { enrollment.stop() }
    }
}

/// The Settings window in the HUD's colours, like the first-run setup: dark, with the content under a
/// transparent title bar.
struct HUDWindowStyle: NSViewRepresentable {
    func makeNSView(context: Context) -> NSView {
        let view = NSView()
        DispatchQueue.main.async { Self.style(view.window) }
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        DispatchQueue.main.async { Self.style(nsView.window) }
    }

    private static func style(_ window: NSWindow?) {
            guard let window else { return }
            window.styleMask.insert(.fullSizeContentView)
            window.titlebarAppearsTransparent = true
            window.titleVisibility = .hidden
            window.isMovableByWindowBackground = true
            window.appearance = NSAppearance(named: .darkAqua)
            window.backgroundColor = NSColor(HUD.background)
            window.toolbar?.showsBaselineSeparator = false
    }
}

/// Impostazioni › Cervello: the sentences the phrase dictionary learned from the model (1.30.88), each
/// with what it now means and a button to forget it when it was learned wrong.
struct LearnedPhrasesSection: View {
    @State private var entries: [(key: String, value: PhraseDictionary.Learned)] = []

    var body: some View {
        Section("Frasi imparate") {
            if entries.isEmpty {
                Caption("Ancora nessuna. Quando una frase breve va al modello e lui fa un solo comando semplice (aprire o chiudere un'app, i promemoria, la musica, la TV…), AL-76 se la ricorda: la volta dopo la fa da solo, senza modello.")
            } else {
                ForEach(entries, id: \.key) { entry in
                    HStack {
                        VStack(alignment: .leading, spacing: 2) {
                            Text("«\(entry.key)»")
                            Text("= \(entry.value.meant)" + (entry.value.uses > 0 ? " · usata \(entry.value.uses) \(entry.value.uses == 1 ? "volta" : "volte")" : ""))
                                .font(.caption).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button("Dimentica") {
                            PhraseDictionary.forget(entry.key)
                            reload()
                        }
                    }
                }
                Caption("Frasi che prima andavano al modello e che ora AL-76 capisce da solo. Se una è sbagliata, «Dimentica»: tornerà al modello.")
            }
        }
        .onAppear(perform: reload)
    }

    private func reload() {
        entries = PhraseDictionary.learned.sorted { $0.value.date > $1.value.date }.map { (key: $0.key, value: $0.value) }
    }
}
