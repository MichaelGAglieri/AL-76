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

    var body: some View {
        Form {
            Section("Attivazione") {
                Toggle("Ascolta sempre \"Ehi Al\" (Whisper, gratuito)", isOn: $draft.whisperWake)
                Text("Se lo spegni AL-76 si attiva solo con ⌃⌥Spazio, e consuma ancora meno.")
                    .font(.caption).foregroundStyle(.secondary)
                SecureField("Picovoice AccessKey (facoltativa)", text: $draft.picovoiceAccessKey)
                Text("Con una chiave Picovoice valida si usa Porcupine al posto di Whisper. Porcupine conosce ancora solo la parola «Jarvis»: per «Ehi Al» va addestrata una parola nuova su Picovoice.")
                    .font(.caption).foregroundStyle(.secondary)
                Slider(value: $draft.wakeSensitivity, in: 0.3...0.9, step: 0.05) {
                    Text("Sensibilità Porcupine \(draft.wakeSensitivity, format: .number.precision(.fractionLength(2)))")
                }
                Toggle("Continua la conversazione senza ripetere \"Ehi Al\"", isOn: $draft.followUpConversation)
                Toggle("Interrompilo a voce (\"basta\")", isOn: $draft.bargeIn)
                Toggle("Cancellazione dell'eco (serve con gli altoparlanti)", isOn: $draft.echoCancellation)
                Text("La cancellazione dell'eco vale dal riavvio di AL-76.").font(.caption).foregroundStyle(.secondary)
            }
            VoicePrintSection(assistant: assistant, voicePrint: assistant.voicePrint, draft: $draft)
            PhoneSection(bridge: assistant.phoneBridge, draft: $draft)
            UniversitySection(assistant: assistant, draft: $draft)
            Section("Stampante Bambu") {
                TextField("Indirizzo IP", text: $draft.bambuHost)
                TextField("Numero di serie", text: $draft.bambuSerial)
                SecureField("Codice di accesso", text: $draft.bambuCode)
                Text("Si trovano sul display della stampante, in Rete e Impostazioni. AL-76 legge lo stato: a che punto è, se finisce o si blocca. Caricare, scaricare il filamento e stampare passano dai pulsanti di Bambu Studio, e per stampare chiede prima conferma.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("Appunti") {
                TextField("Cartelle, una per riga", text: Binding(
                    get: { draft.notesFolders.joined(separator: "\n") },
                    set: { draft.notesFolders = $0.components(separatedBy: "\n") }
                ), axis: .vertical)
                Text("AL-76 cerca dentro PDF, Word, PowerPoint e testi di queste cartelle, tutto sul Mac. Le altre cartelle, come i documenti personali, restano fuori. Le nuove cartelle entrano alla prossima lettura, entro 6 ore o al riavvio.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("Voce") {
                Toggle("Voce neurale (Qwen3 in italiano, Lewis in inglese)", isOn: $draft.neuralVoice)
                if draft.neuralVoice {
                    Slider(value: $draft.kokoroSpeed, in: 0.8...1.2) { Text("Velocità voce inglese") }
                }
                Text("Le voci Apple qui sotto si usano se la voce neurale è spenta o non disponibile.")
                    .font(.caption).foregroundStyle(.secondary)
                Picker("Voce italiana", selection: $draft.italianVoice) {
                    ForEach(italianVoices, id: \.self) { Text($0).tag($0) }
                }
                Picker("Voce inglese", selection: $draft.englishVoice) {
                    ForEach(englishVoices, id: \.self) { Text($0).tag($0) }
                }
                Slider(value: $draft.speechRate, in: 0.4...0.62) { Text("Velocità") }
                Text("Altre voci: Impostazioni di Sistema › Accessibilità › Contenuti letti ad alta voce › Gestisci voci.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("Cervello") {
                TextField("Modello locale (Ollama)", text: $draft.localModel)
                TextField("Indirizzo Ollama", text: $draft.ollamaURL)
                TextField("Il tuo nome", text: $draft.userName)
            }
            Section("Controllo delle app") {
                Toggle("Lascia che AL-76 usi pulsanti e campi delle app", isOn: $draft.appControl)
                Toggle("Touch ID per le azioni molto delicate", isOn: $draft.ownerCheck)
                Text("Script e programmi, comandi rapidi, cancellazione dei ricordi: dopo il \"sì\" serve il dito (o la password del Mac).")
                    .font(.caption).foregroundStyle(.secondary)
                Text("Solo con Claude e fuori dalla modalità privacy. Mai password, banca, Impostazioni di Sistema, terminali e editor. Serve il permesso Accessibilità.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("Iniziativa (AL-76 parla per primo)") {
                Toggle("Appuntamenti: avviso 10 minuti prima", isOn: $draft.alertCalendar)
                Toggle("Promemoria: annuncio alla scadenza", isOn: $draft.alertReminders)
                Toggle("Studio: pausa ogni 2 ore", isOn: $draft.alertStudy)
                Stepper(draft.studySummaryHour == 0 ? "Riepilogo dello studio: spento" : "Riepilogo dello studio alle \(draft.studySummaryHour):00",
                        value: $draft.studySummaryHour, in: 0...22)
                Toggle("Batteria sotto il 20%", isOn: $draft.alertBattery)
                Toggle("Briefing alla prima attivazione del mattino", isOn: $draft.morningBriefing)
                Stepper("Silenzio dalle \(draft.quietFrom):00", value: $draft.quietFrom, in: 0...23)
                Stepper("fino alle \(draft.quietTo):00", value: $draft.quietTo, in: 0...23)
                Text("In privacy, durante una conversazione o con una conferma in sospeso gli avvisi aspettano.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("SATELLITE") {
                Picker("Aspetto del SATELLITE", selection: $draft.nucleusStyle) {
                    Text("Reattore").tag("reattore")
                    Text("Satellite").tag("satellite")
                    Text("Robottino").tag("robottino")
                }
                if draft.nucleusStyle == "robottino" {
                    if RobotNucleusView.modelAvailable {
                        Toggle("Robottino grande", isOn: $draft.robotLarge)
                        Picker("Fiamme dei propulsori", selection: $draft.robotFlames) {
                            Text("Fuoco").tag("fuoco")
                            Text("Ioniche").tag("ioniche")
                        }
                        Toggle("Passeggia sul bordo inferiore quando aspetta", isOn: $draft.robotWalks)
                        Text("Clic: una carezza · doppio clic: l'HUD · trascina per spostarlo. Le risposte scritte (⌃⌥T) escono dalla sua bocca.")
                            .font(.caption).foregroundStyle(.secondary)
                    } else {
                        Text("Manca il modello 3D: metti scene.gltf (con scene.bin e textures) in Application Support/JARVIS/Companion. Intanto si vede il satellite.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
                Toggle("Disco scuro dietro al SATELLITE", isOn: $draft.nucleusBackdrop)
                Text("Spento si fonde con lo sfondo; acceso resta leggibile su sfondi chiari o sopra le finestre.")
                    .font(.caption).foregroundStyle(.secondary)
            }
            Section("Sistema") {
                Toggle("Saluta all'avvio", isOn: $draft.greetOnLaunch)
                Toggle("Avvia AL-76 al login", isOn: $launchAtLogin)
                    .onChange(of: launchAtLogin) { _, enabled in
                        try? enabled ? SMAppService.mainApp.register() : SMAppService.mainApp.unregister()
                    }
            }
            HStack {
                Spacer()
                Button("Salva") { assistant.apply(draft) }
                    .keyboardShortcut(.defaultAction)
                    .disabled(draft == assistant.config)
            }
        }
        .formStyle(.grouped)
        .frame(width: 520, height: 620)
        .onAppear { draft = assistant.config }
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
private struct UniversitySection: View {
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

private struct VoiceEnrollmentView: View {
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
