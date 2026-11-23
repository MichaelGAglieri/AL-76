import Foundation
import Testing
@testable import JARVIS

/// The commands that run without a model (InstantCommand, DirectCommand) and the checks on the answers to
/// AL-76's own questions. The phrases are invented examples, the way Whisper writes them (capitals, full
/// stops, "Ehi Al," in front): never phrases copied from the logs, which stay on this Mac (see ReplayTests).
@Suite("Comandi senza modello")
struct ParserTests {

    // MARK: Instant commands

    @Test("Comandi istantanei", arguments: [
        ("Ehi Al, apri Safari.", InstantCommand.open("safari")),
        ("Chiudi Safari", .close("safari")),
        ("apri le note", .open("note")),
        ("Alza il volume", .changeVolume(10)),
        ("volume al 50", .setVolume(50)),
        ("Metti un timer di 5 minuti", .timer(seconds: 300, label: nil)),
        ("timer di un'ora e mezza", .timer(seconds: 5400, label: nil)),
        ("Che ore sono?", .time),
        ("chireson", .time),
        ("carica il filamento", .filament(load: true)),
        ("metti in pausa la musica", .music(.pause)),
        ("prossima canzone", .music(.next)),
        ("accendi la TV", .tv(on: true)),
        ("spegni la televisione", .tv(on: false)),
        ("minimizza Safari", .minimize("safari")),
        ("resta nell'angolo", .robotPin(true)),
        ("puoi camminare", .robotPin(false)),
        // An app whose name has "PDF" in it: not a file to look for (1.30.85).
        ("Apri PDF Craft", .open("pdf craft")),
        ("apri Word", .open("word")),
    ])
    func instant(_ phrase: String, _ expected: InstantCommand) {
        #expect(InstantCommand.parse(phrase) == expected)
    }

    // MARK: Direct commands

    @Test("Comandi diretti", arguments: [
        ("avvia una sessione di studio di Fisica 2", DirectCommand.startSession(name: "Fisica 2", category: "study")),
        ("ferma la sessione", .stopSession),
        ("elimina l'ultima sessione", .deleteSession(name: nil)),
        ("pausa di 10 minuti", .pauseSession(minutes: 10)),
        ("riprendi la sessione", .resumeSession),
        ("piano di studio", .studyPlan),
        ("ci sono mail importanti?", .mail),
        ("quante mail ho?", .mailCount),
        ("che promemoria ho?", .reminders),
        ("che tempo fa a Milano domani?", .weather(place: "Milano", tomorrow: true)),
        ("quanta batteria ho?", .battery),
        ("che temperatura ha il Mac?", .temperature),
        ("che giorno è oggi?", .date),
        ("quanto fa 781 per 28?", .math("781 * 28")),
        ("metti Bohemian Rhapsody", .music("bohemian rhapsody")),
        ("crea una cartella Esercizi sulla scrivania", .folder(name: "Esercizi", place: "~/Desktop")),
        ("apri la cartella Download", .openFolder("download")),
        ("spegni lo schermo", .sleepDisplay),
        ("attiva la modalità scura", .darkMode(true)),
        ("che lezioni ho domani?", .lessons(offset: 1, days: 1)),
        ("qual è la mia media?", .booklet(.average)),
    ])
    func direct(_ phrase: String, _ expected: DirectCommand) {
        #expect(InstantCommand.parse(phrase) == nil, "un comando istantaneo lo prenderebbe prima")
        #expect(DirectCommand.parse(phrase) == expected)
    }

    @Test("Promemoria con data")
    func reminderWithDate() throws {
        guard case .reminder(let title, let due) = DirectCommand.parse("ricordami di chiamare Marco domani alle 9") else {
            Issue.record("non riconosciuto come promemoria"); return
        }
        #expect(title == "Chiamare Marco")
        let date = try #require(due)
        let tomorrow = Calendar.current.date(byAdding: .day, value: 1, to: Date())!
        #expect(Calendar.current.isDate(date, inSameDayAs: tomorrow))
        #expect(Calendar.current.component(.hour, from: date) == 9)
    }

    @Test("Il calcolo è giusto (Claude aveva sbagliato 781 × 28)")
    func arithmetic() {
        #expect(DirectCommand.compute("781 * 28")?.replacingOccurrences(of: ".", with: "") == "21868")
    }

    @Test("Domande libere: vanno al modello", arguments: [
        "raccontami una barzelletta", "spiegami la legge di Ohm", "ok grazie",
    ])
    func toTheModel(_ phrase: String) {
        #expect(InstantCommand.parse(phrase) == nil)
        #expect(DirectCommand.parse(phrase) == nil)
    }

    // MARK: Answers to AL-76's questions

    @Test("«No» al resoconto, anche lungo o storpiato (1.30.82)", arguments: [
        "No.", "Niente.", "Nulla.", "Mulla.", "Nula", "Nullo.", "No. No.", "Nulla nulla", "No, niente grazie.",
        "Non scrivere niente.", "Nulla, non scriverai nulla nel resoconto.", "Nessun resoconto per oggi.",
        "Senza resoconto.", "Non serve niente, grazie.", "Ehi Al, niente resoconto.",
    ])
    func noReport(_ phrase: String) {
        #expect(DirectCommand.isNoReport(phrase))
    }

    @Test("Resoconti veri: restano resoconti", arguments: [
        "Integrali.", "Molla.", "Bulloni", "No, nel resoconto scrivi integrali doppi.",
        "Nel resoconto scrivi esercizi di dinamica e conservazione dell'energia.",
        "Ho fatto gli esercizi sui circuiti RC.", "Scrivi che ho finito il capitolo tre.",
    ])
    func realReport(_ phrase: String) {
        #expect(!DirectCommand.isNoReport(phrase))
    }

    @Test("Sì e no")
    func yesNo() {
        for yes in ["Sì.", "Certo", "va bene", "Sì, procedi."] { #expect(DirectCommand.isYes(yes), "\(yes)") }
        for no in ["No.", "no grazie", "lascia stare", "Annulla."] { #expect(DirectCommand.isNo(no), "\(no)") }
        #expect(!DirectCommand.isYes("No."))
        #expect(!DirectCommand.isNo("Sì."))
    }

    @Test("Una frase che comincia con un ordine non è un resoconto")
    func request() {
        #expect(DirectCommand.isRequest("Apri il PDF di meccanica e avvia una sessione"))
        #expect(!DirectCommand.isRequest("Scrivi nel resoconto: integrali"))
        #expect(!DirectCommand.isRequest("Ho fatto gli integrali"))
    }

    @Test("«Sta ancora studiando?»")
    func stillStudying() throws {
        let now = Self.today(at: 18)
        #expect(DirectCommand.studyAnswer("sì", now: now) == .yes)
        #expect(DirectCommand.studyAnswer("sto ancora studiando", now: now) == .yes)
        #expect(DirectCommand.studyAnswer("no grazie", now: now) == .no(end: nil))
        #expect(DirectCommand.studyAnswer("no, ho smesso alle 17", now: now) == .no(end: Self.today(at: 17)))
        guard case .no(let end)? = DirectCommand.studyAnswer("ho finito mezz'ora fa", now: now) else {
            Issue.record("non riconosciuto come no"); return
        }
        #expect(abs(try #require(end).timeIntervalSince(now) + 1800) < 60)
        #expect(DirectCommand.studyAnswer("apri Safari", now: now) == nil)
    }

    @Test("Orario già passato: alle 17 dette alle 16 è ieri")
    func pastTime() {
        let now = Self.today(at: 16)
        let yesterday = Calendar.current.date(byAdding: .day, value: -1, to: Self.today(at: 17))!
        #expect(DirectCommand.pastTime("ho smesso alle 17", now: now) == yesterday)
    }

    // MARK: Wake word

    @Test("Chiamata «Ehi Al»")
    func call() {
        #expect(WakeVerifier.startsWithCall("Ehi Al, apri Safari"))
        #expect(WakeVerifier.strippingCall("Ehi Al, apri Safari") == "apri Safari")
        #expect(!WakeVerifier.startsWithCall("apri Safari"))
        // "Jarvis" is no longer the name since 1.30.64.
        #expect(!WakeVerifier.startsWithCall("Jarvis che ore sono"))
    }

    @Test("Voti del libretto a parole")
    func grades() {
        #expect(Booklet.grade(from: "28") == "28")
        #expect(Booklet.grade(from: "trenta e lode") == "30L")
        #expect(Booklet.grade(from: "ventotto") == "28")
        #expect(Booklet.grade(from: "diciassette") == nil)
        #expect(Booklet.grade(from: "idoneo") == "Idoneo")
    }

    private static func today(at hour: Int) -> Date {
        Calendar.current.date(bySettingHour: hour, minute: 0, second: 0, of: Date())!
    }
}
