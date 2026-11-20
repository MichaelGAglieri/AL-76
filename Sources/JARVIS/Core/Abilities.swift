import Foundation

/// What AL-76 may do, switch by switch (Impostazioni › Permessi, v1.30.74, user request 2026-11-17).
/// Each ability groups the tools behind it: a switched-off ability refuses those tools wherever the
/// request comes from (voice, writing bar, iPhone, any brain). Reading the state, stopping and
/// timers are never blocked: a robot must always be stoppable.
enum Ability: String, CaseIterable, Identifiable, Codable {
    case systemControl, appControl, shortcuts
    case screen, camera
    case filesRead, filesWrite, code
    case web
    case mailRead, mailSend, calendar
    case memory, university
    case robot, arduino, printer

    var id: String { rawValue }

    var title: String {
        switch self {
        case .systemControl: return "Comandare il Mac"
        case .appControl: return "Usare pulsanti e campi delle app"
        case .shortcuts: return "Comandi rapidi"
        case .screen: return "Guardare lo schermo"
        case .camera: return "Usare la fotocamera"
        case .filesRead: return "Cercare e aprire file"
        case .filesWrite: return "Creare e rinominare file"
        case .code: return "Leggere e modificare codice"
        case .web: return "Cercare e leggere sul web"
        case .mailRead: return "Leggere la posta"
        case .mailSend: return "Mandare email"
        case .calendar: return "Calendario e promemoria"
        case .memory: return "Ricordare cose su di te"
        case .university: return "Portale dell'università"
        case .robot: return "Muovere il braccio robotico"
        case .arduino: return "Schede Arduino"
        case .printer: return "Comandare la stampante 3D"
        }
    }

    var icon: String {
        switch self {
        case .systemControl: return "macwindow"
        case .appControl: return "hand.tap"
        case .shortcuts: return "square.stack.3d.up"
        case .screen: return "rectangle.dashed"
        case .camera: return "camera"
        case .filesRead: return "doc.text.magnifyingglass"
        case .filesWrite: return "doc.badge.plus"
        case .code: return "chevron.left.forwardslash.chevron.right"
        case .web: return "globe"
        case .mailRead: return "envelope"
        case .mailSend: return "paperplane"
        case .calendar: return "calendar"
        case .memory: return "brain.head.profile"
        case .university: return "graduationcap"
        case .robot: return "gearshape.2"
        case .arduino: return "cpu"
        case .printer: return "printer"
        }
    }

    var explanation: String {
        switch self {
        case .systemControl: return "Aprire e chiudere app, volume, modalità scura, spegnere lo schermo, musica e TV."
        case .appControl: return "Premere pulsanti e scrivere nelle altre app, per esempio un messaggio su WhatsApp. Solo con Claude, ChatGPT o Gemini; mai in password, banca e Impostazioni."
        case .shortcuts: return "Avviare i Comandi rapidi di Apple. Dopo il «sì» serve il Touch ID."
        case .screen: return "«Guarda il mio schermo»: una foto dello schermo mandata al cervello in uso."
        case .camera: return "«Guardami»: una foto dalla fotocamera del Mac mandata al cervello in uso."
        case .filesRead: return "Cercare file e appunti, aprire cartelle e progetti, leggere i PDF."
        case .filesWrite: return "Creare PDF, schemi e cartelle, unire o dividere PDF, rinominare file. Non cancella mai niente."
        case .code: return "Leggere, modificare e ripristinare il codice dei progetti, scaricare repository da GitHub."
        case .web: return "Cercare su internet, leggere pagine, aprire link e tenere d'occhio un sito. Meteo e notizie restano."
        case .mailRead: return "Leggere e cercare nella posta di Mail, anche nel briefing del mattino."
        case .mailSend: return "Scrivere e mandare email. Chiede sempre il «sì» prima di spedire."
        case .calendar: return "Leggere, creare e cancellare appuntamenti e promemoria, anche nel briefing."
        case .memory: return "Salvare e cancellare i ricordi su di te. Quelli già salvati si possono sempre leggere."
        case .university: return "Lezioni, agenda e libretto dal portale dell'università, in sola lettura."
        case .robot: return "Muovere giunti e sequenze, ROS 2 e laboratorio. Fermare il robot e leggerne lo stato funziona sempre."
        case .arduino: return "Compilare e caricare programmi sulle schede, leggere la porta seriale."
        case .printer: return "Aprire Bambu Studio, caricare il filamento e preparare le stampe. Lo stato della stampante si legge sempre."
        }
    }

    /// The section of the settings page the switch appears in.
    var group: String {
        switch self {
        case .systemControl, .appControl, .shortcuts: return "Il Mac e le app"
        case .screen, .camera: return "Vedere"
        case .filesRead, .filesWrite, .code: return "File e codice"
        case .web, .mailRead, .mailSend, .calendar: return "Internet, posta e calendario"
        case .memory, .university: return "Dati personali"
        case .robot, .arduino, .printer: return "Robotica e stampa 3D"
        }
    }

    static var groups: [String] {
        var seen: [String] = []
        for ability in allCases where !seen.contains(ability.group) { seen.append(ability.group) }
        return seen
    }

    var tools: Set<String> {
        switch self {
        case .systemControl: return ["open_app", "quit_app", "set_volume", "set_dark_mode", "sleep_display", "music", "tv"]
        case .appControl: return ["app_read", "app_press", "app_type", "app_key", "app_scroll", "app_look", "app_click_at"]
        case .shortcuts: return ["run_shortcut"]
        case .screen: return ["look_at_screen"]
        case .camera: return ["look_at_camera"]
        case .filesRead: return ["search_files", "open_path", "search_notes", "pdf_info", "list_projects", "project_info", "recent_project_activity"]
        case .filesWrite: return ["merge_pdfs", "create_pdf", "create_folder", "extract_pdf_pages", "rename_file", "webpage_to_pdf", "draw_diagram"]
        case .code: return ["read_code", "edit_code", "restore_code", "clone_repository"]
        case .web: return ["web_search", "read_webpage", "open_url", "watch"]
        case .mailRead: return ["list_unread_emails", "search_emails", "read_email", "set_mail_sender"]
        case .mailSend: return ["send_email"]
        case .calendar: return ["list_events", "create_event", "delete_event", "list_reminders", "create_reminder", "complete_reminder", "delete_reminder"]
        case .memory: return ["remember", "forget"]
        case .university: return ["university"]
        case .robot: return ["robot_enable", "robot_joint", "robot_sequence", "robot_home", "robot_plan", "robot_execute",
                             "robot_connect", "robot_close", "ros_publish", "lab_start", "lab_command"]
        case .arduino: return ["arduino_compile", "arduino_upload", "serial_read"]
        case .printer: return ["printer_filament", "printer_print", "bambu_open"]
        }
    }

    /// Never blocked, whatever the switches say: stopping and reading the state are safety.
    static let alwaysAllowed: Set<String> = ["robot_stop", "robot_status", "printer_status", "lab_status", "cancel_timer"]

    /// The ability a tool belongs to (nil = always allowed).
    static func of(tool name: String) -> Ability? {
        guard !alwaysAllowed.contains(name) else { return nil }
        return allCases.first { $0.tools.contains(name) }
    }

    /// The sentence the brain receives instead of the result.
    var refusal: String {
        "L'utente ha spento il permesso «\(title)» in Impostazioni › Permessi di AL-76: non posso farlo. Diglielo in una frase e, se vuole, che lo riaccenda lì."
    }
}

extension JarvisConfig {
    /// The abilities switched off. App control keeps its old switch (appControl), shared with this page.
    var blocked: Set<Ability> {
        var set = Set(blockedAbilities.compactMap(Ability.init(rawValue:)))
        if !appControl { set.insert(.appControl) }
        return set
    }

    func allows(_ ability: Ability) -> Bool { !blocked.contains(ability) }

    mutating func set(_ ability: Ability, allowed: Bool) {
        if ability == .appControl { appControl = allowed; return }
        var list = blockedAbilities.filter { $0 != ability.rawValue && Ability(rawValue: $0) != nil }
        if !allowed { list.append(ability.rawValue) }
        blockedAbilities = Ability.allCases.map(\.rawValue).filter(list.contains)
    }
}
