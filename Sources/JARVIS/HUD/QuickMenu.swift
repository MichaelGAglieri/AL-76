import AppKit
import Carbon

/// A menu row that runs a closure.
@MainActor
final class BlockMenuItem: NSMenuItem {
    private let block: () -> Void

    init(_ title: String, checked: Bool = false, enabled: Bool = true, key: String = "",
         modifiers: NSEvent.ModifierFlags = [.control, .option], _ block: @escaping () -> Void) {
        self.block = block
        super.init(title: title, action: #selector(run), keyEquivalent: key)
        keyEquivalentModifierMask = key.isEmpty ? [] : modifiers
        target = self
        state = checked ? .on : .off
        isEnabled = enabled
    }

    required init(coder: NSCoder) { fatalError() }

    @objc private func run() { block() }
}

/// The one quick menu (1.30.81, 23 November, user request): the same rows, in the same order, on the
/// menu-bar icon and on the right click of the robot, the satellite, the reactor and the sphere.
/// It is built again every time it opens, so ticks and names are always current.
@MainActor
enum QuickMenu {
    static weak var assistant: Assistant?
    /// Set by the app: whether the sphere is open, and how to open or close it.
    static var sphereOpen: () -> Bool = { false }
    static var openSphere: () -> Void = {}
    static var closeSphere: () -> Void = {}
    static var showOnboarding: () -> Void = {}

    static func make() -> NSMenu {
        let menu = NSMenu()
        fill(menu)
        return menu
    }

    static func fill(_ menu: NSMenu) {
        menu.removeAllItems()
        menu.autoenablesItems = false
        guard let assistant else { return }

        let head = NSMenuItem(title: "AL-76 · " + headline(assistant), action: nil, keyEquivalent: "")
        head.isEnabled = false
        menu.addItem(head)
        menu.addItem(.separator())

        menu.addItem(BlockMenuItem("Parla", key: " ") { assistant.pushToTalk() })
        menu.addItem(BlockMenuItem(TypeBar.main?.isVisible == true ? "Nascondi barra di scrittura" : "Scrivi", key: "t") {
            TypeBar.main?.flip()
        })
        menu.addItem(BlockMenuItem("Nuova conversazione") { assistant.resetConversation() })
        menu.addItem(.separator())

        menu.addItem(BlockMenuItem("Apri plancia", key: "k") { Plancia.main?.open() })
        let sections = NSMenu()
        for area in PlanciaArea.allCases {
            let detached = Plancia.main?.detached.contains(area) == true
            sections.addItem(BlockMenuItem(area.title + (detached ? " (finestrella)" : "")) { Plancia.main?.open(area) })
        }
        menu.addItem(submenu("Sezioni", sections))
        menu.addItem(.separator())

        let brains = NSMenu()
        for (brain, title) in [(Assistant.Brain.local, "Nucleo locale"), (.claude, "Claude"), (.chatgpt, "ChatGPT"), (.gemini, "Gemini")] {
            brains.addItem(BlockMenuItem(title, checked: assistant.brain == brain) { assistant.setBrain(brain) })
        }
        if assistant.brain == .claude {
            brains.addItem(.separator())
            for model in Assistant.claudeModels {
                brains.addItem(BlockMenuItem("Modello " + model.capitalized, checked: assistant.claudeModel == model) {
                    assistant.setClaudeModel(model)
                })
            }
        }
        brains.addItem(.separator())
        brains.addItem(BlockMenuItem("Senza IA: solo i comandi che conosco", checked: assistant.config.noAI) {
            assistant.setArtificialIntelligence(assistant.config.noAI)
        })
        menu.addItem(submenu("Cervello", brains))
        menu.addItem(BlockMenuItem("Modalità privacy", checked: assistant.privacyMode, key: "p") { assistant.togglePrivacy() })
        menu.addItem(.separator())

        let style = assistant.config.nucleusStyle
        let small = style == "robottino" && RobotNucleusView.modelAvailable ? "robottino" : (style == "reattore" ? "reattore" : "satellite")
        if sphereOpen() {
            menu.addItem(BlockMenuItem("Riduci a \(small)") { closeSphere() })
        } else {
            menu.addItem(BlockMenuItem("Apri la sfera") { openSphere() })
        }
        if small == "robottino", let robot = RobotNucleusView.main {
            menu.addItem(submenu("Robottino", robotMenu(robot, assistant: assistant)))
        }
        menu.addItem(.separator())

        menu.addItem(BlockMenuItem("Impostazioni…", key: ",", modifiers: [.command]) { openSettings() })
        menu.addItem(BlockMenuItem("Configurazione guidata…") { showOnboarding() })
        menu.addItem(BlockMenuItem("Esci da AL-76") { NSApp.terminate(nil) })
    }

    /// The robot's own rows (17 and 19 November): corner lock and speech bubble. The bubble itself is unchanged.
    private static func robotMenu(_ robot: RobotNucleusView, assistant: Assistant) -> NSMenu {
        let menu = NSMenu()
        menu.autoenablesItems = false
        let pinned = assistant.config.robotPinned
        menu.addItem(BlockMenuItem("Blocca nell'angolo in basso a destra", checked: pinned) { assistant.setRobotPinned(!pinned) })
        menu.addItem(.separator())
        if robot.bubbleShown {
            menu.addItem(BlockMenuItem("Nascondi nuvoletta") { robot.hideBubble() })
        } else {
            menu.addItem(BlockMenuItem("Mostra nuvoletta", enabled: robot.canShowBubble) { robot.showBubbleAgain() })
        }
        menu.addItem(BlockMenuItem("Nuvoletta anche per le risposte a voce", checked: assistant.voiceBubble) { robot.toggleVoiceBubble() })
        return menu
    }

    private static func submenu(_ title: String, _ menu: NSMenu) -> NSMenuItem {
        let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
        item.submenu = menu
        return item
    }

    /// "In attesa · Claude", or the privacy notice.
    static func headline(_ assistant: Assistant) -> String {
        if assistant.privacyMode { return "privacy attiva" }
        let brain = assistant.config.noAI ? "senza IA" : (assistant.answering ?? assistant.brain).title
        return stateWord(assistant.state).lowercased() + " · " + brain
    }

    /// The state in plain words (the sphere's labels are capitals for the HUD).
    static func stateWord(_ state: Assistant.State) -> String {
        switch state {
        case .booting: return "Avvio"
        case .idle: return "In attesa"
        case .listening: return "In ascolto"
        case .transcribing: return "Trascrive"
        case .thinking: return "Pensa"
        case .acting: return "Esegue"
        case .speaking: return "Risponde"
        case .error: return "Anomalia"
        }
    }

    /// The rows as text, submenus in brackets and ticks as ✓: for the test channel.
    static func describe(_ menu: NSMenu? = nil) -> String {
        let menu = menu ?? make()
        return menu.items.map { item in
            if item.isSeparatorItem { return "|" }
            let tick = item.state == .on ? "✓" : ""
            return tick + item.title + (item.submenu.map { " [" + describe($0) + "]" } ?? "")
        }.joined(separator: " · ")
    }

    /// The Settings are a SwiftUI scene: from an AppKit menu they open through the app menu's own row (⌘,).
    static func openSettings() {
        NSApp.activate()
        let rows = NSApp.mainMenu?.items.first?.submenu?.items ?? []
        if let row = rows.first(where: { $0.keyEquivalent == "," && $0.keyEquivalentModifierMask == .command }),
           let action = row.action {
            NSApp.sendAction(action, to: row.target, from: row)
        }
    }
}

/// Catches only the right click (and control-click) over a SwiftUI view and shows the quick menu;
/// every other click goes through to the view underneath. Used by the sphere's window.
final class QuickMenuCatcher: NSView {
    override func hitTest(_ point: NSPoint) -> NSView? {
        guard let event = NSApp.currentEvent else { return nil }
        let right = event.type == .rightMouseDown
            || (event.type == .leftMouseDown && event.modifierFlags.contains(.control))
        return right ? super.hitTest(point) : nil
    }

    override func rightMouseDown(with event: NSEvent) { pop(event) }
    override func mouseDown(with event: NSEvent) { pop(event) }

    private func pop(_ event: NSEvent) {
        MainActor.assumeIsolated { NSMenu.popUpContextMenu(QuickMenu.make(), with: event, for: self) }
    }
}
