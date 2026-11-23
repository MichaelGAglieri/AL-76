import Foundation
import Testing
@testable import JARVIS

/// "Senza IA" (1.30.84): the switch by voice, the examples AL-76 suggests and the split of compound requests.
@Suite("Senza IA")
struct NoAITests {
    @Test("Spegnere l'IA a voce", arguments: [
        "Disattiva l'IA.", "Ehi Al, spegni l'IA", "disattiva l'intelligenza artificiale", "Modalità senza IA", "lavora senza IA",
        "disabilita la IA",
    ])
    func off(_ phrase: String) {
        #expect(InstantCommand.parse(phrase) == .artificialIntelligence(on: false))
    }

    @Test("Riaccenderla", arguments: ["Riattiva l'IA.", "accendi l'IA", "riattiva l'intelligenza artificiale", "torna con l'IA", "usa di nuovo l'IA"])
    func on(_ phrase: String) {
        #expect(InstantCommand.parse(phrase) == .artificialIntelligence(on: true))
    }

    @Test("Una domanda che nomina l'IA non è l'interruttore", arguments: [
        "spiegami come funziona un robot senza IA", "cos'è l'intelligenza artificiale", "scarica il modello dalla memoria",
    ])
    func notTheSwitch(_ phrase: String) {
        #expect(InstantCommand.parse(phrase) != .artificialIntelligence(on: false))
        #expect(InstantCommand.parse(phrase) != .artificialIntelligence(on: true))
    }

    @Test("Le parole del menu del robottino (23 novembre, erano finite a Claude)", arguments: [
        "Blocca lì in basso a destra.", "blocca nell'angolo in basso a destra", "resta in basso a destra", "mettiti nell'angolo",
    ])
    func robotCorner(_ phrase: String) {
        #expect(InstantCommand.parse(phrase) == .robotPin(true))
    }

    @Test("Ogni esempio suggerito è un comando che conosco")
    func examplesWork() {
        let all = KnownCommands.topics.flatMap(\.examples) + KnownCommands.general
        for example in all {
            #expect(InstantCommand.parse(example) != nil || DirectCommand.parse(example) != nil, "«\(example)» non è capito")
        }
    }

    @Test("Suggerimenti vicini alla richiesta")
    func suggestions() {
        #expect(KnownCommands.suggestions(for: "riassumimi le mail di oggi").contains("ci sono mail importanti?"))
        #expect(KnownCommands.suggestions(for: "che tempo farà nel weekend al mare?").contains("che tempo fa domani?"))
        #expect(KnownCommands.suggestions(for: "scrivimi una poesia").isEmpty)
        #expect(KnownCommands.suggestions(for: "spiegami il teorema di Thevenin").count <= 2)
    }

    @Test("Richieste composte divise in parti")
    func parts() {
        #expect(KnownCommands.parts(of: "Apri Safari e metti un timer di 5 minuti.") == ["Apri Safari", "metti un timer di 5 minuti"])
        #expect(KnownCommands.parts(of: "apri Safari, poi alza il volume e dimmi che ore sono") == ["apri Safari", "alza il volume", "dimmi che ore sono"])
        #expect(KnownCommands.parts(of: "apri Safari") == nil)
        let parts = KnownCommands.parts(of: "apri Safari e scrivimi una poesia sul mare") ?? []
        #expect(parts.count == 2)
        #expect(InstantCommand.parse(parts[0]) != nil)
    }
}

@Suite("Senza IA · suggerimenti")
struct NoAISuggestionTests {
    @Test("Il tema conta più del verbo")
    func topicOverVerb() {
        let examples = KnownCommands.suggestions(for: "apri il sito del meteo e dimmi le notizie")
        #expect(examples.first == "che tempo fa domani?")
    }
}

/// 23 November, first real use without AI: phrases the logs showed going nowhere.
@Suite("Senza IA · frasi vere del primo uso")
struct NoAIFirstUseTests {
    @Test("Chiamata scritta «Ehi, allo» e promemoria detti in altri modi", arguments: [
        "Ehi, allo, che promemoria ho?", "Leggimi promemoria.", "Che pro-memorial.", "leggimi i miei promemoria",
    ])
    func reminders(_ phrase: String) {
        #expect(DirectCommand.parse(phrase) == .reminders)
    }

    @Test("Aprire un file per nome", arguments: [
        ("apri il PDF di chimica organica", DirectCommand.openFile(query: "chimica organica", kind: "pdf")),
        ("Apri la dispensa di fisica 2.", .openFile(query: "fisica 2", kind: "pdf")),
        ("aprimi le slide di elettronica", .openFile(query: "elettronica", kind: "slide")),
        ("apri il file relazione laboratorio", .openFile(query: "relazione laboratorio", kind: "")),
    ])
    func openFile(_ phrase: String, _ expected: DirectCommand) {
        #expect(InstantCommand.parse(phrase) == nil)
        #expect(DirectCommand.parse(phrase) == expected)
    }

    @Test("La cartella resta cartella")
    func folderStays() {
        #expect(DirectCommand.parse("apri la cartella Download") == .openFolder("download"))
    }

    @Test("Il nome del file contiene tutte le parole chieste")
    func nameMatch() {
        #expect(Assistant.nameHasAll("chimica organica", "/x/Chimica_Organica_Dispense.pdf"))
        #expect(Assistant.nameHasAll("dispense chimica", "/x/Dispensa Chimica.pdf"))
        #expect(!Assistant.nameHasAll("chimica organica", "/x/Chimica_Generale.pdf"))
        #expect(Assistant.nameHasAll("fisica 2", "/x/Fisica 2 esercizi.pdf"))
        #expect(!Assistant.nameHasAll("fisica 2", "/x/Fisica 1 esercizi.pdf"))
        #expect(Assistant.spokenFileName("/x/Chimica_Organica-Dispense.pdf") == "Chimica Organica Dispense")
    }
}
