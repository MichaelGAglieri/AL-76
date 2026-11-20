import AppKit

/// A menu row that runs a closure: the right-click menus of the ROBOTTINO and the SATELLITE.
@MainActor
final class BlockMenuItem: NSMenuItem {
    private let block: () -> Void

    init(_ title: String, checked: Bool = false, enabled: Bool = true, _ block: @escaping () -> Void) {
        self.block = block
        super.init(title: title, action: #selector(run), keyEquivalent: "")
        target = self
        state = checked ? .on : .off
        isEnabled = enabled
    }

    required init(coder: NSCoder) { fatalError() }

    @objc private func run() { block() }
}

/// Rows shared by the right-click menus (17 November, user request): the typing bar and the
/// conversation open and close from here, without the shortcut every time.
@MainActor
enum NucleusMenu {
    static var typeBarTitle: String {
        TypeBar.main?.isVisible == true ? "Nascondi barra di scrittura (⌃⌥T)" : "Mostra barra di scrittura (⌃⌥T)"
    }

    static func typeBarItem() -> NSMenuItem {
        BlockMenuItem(typeBarTitle) { TypeBar.main?.flip() }
    }

    static var dialogueOpen: Bool { PanelWindows.main?.isOpen(.transcript) == true }

    /// The "Frasi" panel: what was said, both sides.
    static func dialogueItem() -> NSMenuItem {
        BlockMenuItem("Mostra dialogo", checked: dialogueOpen) { PanelWindows.main?.flip(.transcript) }
    }
}
