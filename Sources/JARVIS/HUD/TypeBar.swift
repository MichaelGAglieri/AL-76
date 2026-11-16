import AppKit
import Carbon.HIToolbox
import Combine
import SwiftUI

extension Notification.Name {
    /// Asks to open the writing bar (satellite menu, menu bar).
    static let jarvisTypeBar = Notification.Name("jarvisTypeBar")
}

/// The writing bar: a small capsule to type to JARVIS instead of speaking (15 November).
/// With the SATELLITE it hangs right under it and follows it; with the sphere's window it starts
/// under the sphere and can be dragged anywhere (the place is remembered, relative to the window).
/// ⌃⌥T opens and closes it, Esc closes it, and it leaves by itself when it is not used.
@MainActor
final class TypeBar: NSObject {
    /// What the bar hangs from right now.
    struct Anchor {
        let window: NSWindow
        /// True for the sphere's window, false for a SATELLITE.
        let isHUD: Bool
    }

    static let width: CGFloat = 320
    private let assistant: Assistant
    private let model = TypeBarModel()
    private var panel: TypeBarPanel?
    private var hotKey: HotKey?
    /// Where JARVIS is on screen now; set by the app delegate.
    var anchor: () -> Anchor? = { nil }
    private weak var followed: NSWindow?
    private var isHUD = false
    private var contentSize = NSSize(width: TypeBar.width, height: 44)
    private var idle: Timer?
    private var watchers: Set<AnyCancellable> = []
    private var observers: [NSObjectProtocol] = []

    /// Offset of the bar from the sphere's window, once it has been dragged.
    private var hudOffset: NSPoint? {
        get {
            let d = UserDefaults.standard
            guard d.object(forKey: "typeBarOffsetX") != nil else { return nil }
            return NSPoint(x: d.double(forKey: "typeBarOffsetX"), y: d.double(forKey: "typeBarOffsetY"))
        }
        set {
            let d = UserDefaults.standard
            if let newValue {
                d.set(newValue.x, forKey: "typeBarOffsetX")
                d.set(newValue.y, forKey: "typeBarOffsetY")
            } else {
                d.removeObject(forKey: "typeBarOffsetX")
                d.removeObject(forKey: "typeBarOffsetY")
            }
        }
    }

    init(assistant: Assistant) {
        self.assistant = assistant
        super.init()
        hotKey = HotKey(keyCode: kVK_ANSI_T) { [weak self] in
            Task { @MainActor in self?.toggle() }
        }
        observers.append(NotificationCenter.default.addObserver(forName: .jarvisTypeBar, object: nil, queue: .main) { [weak self] note in
            let text = note.object as? String
            Task { @MainActor in
                self?.show()
                // Test mode: a text that arrives with the request is sent as if typed.
                if let text, !text.isEmpty { _ = self?.send(text) }
            }
        })
        // The bar follows whatever it hangs from, and changes side when the HUD opens or closes.
        for name in [NSWindow.didMoveNotification, NSWindow.didResizeNotification] {
            observers.append(NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { [weak self] note in
                Task { @MainActor in
                    guard let self, let moved = note.object as? NSWindow, moved === self.followed else { return }
                    self.place()
                }
            })
        }
        // Answers and work in progress count as use: it does not leave in the middle of an answer.
        assistant.$state.map(\.wireStateIsIdle).removeDuplicates().sink { [weak self] _ in self?.touch() }.store(in: &watchers)
    }

    var isVisible: Bool { panel?.isVisible ?? false }

    func toggle() {
        if let panel, panel.isVisible, panel.isKeyWindow { hide() } else { show() }
    }

    func show() {
        let panel = panel ?? makePanel()
        self.panel = panel
        place()
        panel.alphaValue = 1
        panel.makeKeyAndOrderFront(nil)
        model.focus += 1
        touch()
    }

    func hide() {
        idle?.invalidate()
        idle = nil
        panel?.orderOut(nil)
        model.asked = false
    }

    /// The HUD opened or closed, or the satellites changed: hang from the right one.
    func anchorChanged() {
        guard isVisible else { return }
        place()
    }

    private func makePanel() -> TypeBarPanel {
        let panel = TypeBarPanel(contentRect: NSRect(origin: .zero, size: contentSize),
                                 styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.level = .floating
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        // Also over a full-screen app: the shortcut is pressed from anywhere.
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .ignoresCycle]
        panel.onCancel = { [weak self] in self?.hide() }
        let view = TypeBarView(assistant: assistant, model: model,
                               onSend: { [weak self] text in self?.send(text) ?? false },
                               onActivity: { [weak self] in self?.touch() },
                               onSize: { [weak self] size in self?.resized(size) },
                               onDrag: { [weak self] phase in self?.dragged(phase) },
                               onClose: { [weak self] in self?.hide() })
        let host = NSHostingView(rootView: view)
        host.safeAreaRegions = []
        host.sizingOptions = []
        panel.contentView = host
        return panel
    }

    private func send(_ text: String) -> Bool {
        guard assistant.typed(text) else { return false }
        model.asked = true
        touch()
        return true
    }

    // MARK: Place

    /// True when the bar sits above what it hangs from: the answer then grows upwards.
    private var above = false

    private func place() {
        guard let panel else { return }
        guard let anchor = anchor() else {
            // Nothing to hang from (it should not happen): top right of the main display.
            if let area = NSScreen.main?.visibleFrame {
                panel.setFrame(NSRect(x: area.maxX - contentSize.width - 12, y: area.maxY - contentSize.height - 12,
                                      width: contentSize.width, height: contentSize.height), display: true)
            }
            return
        }
        followed = anchor.window
        isHUD = anchor.isHUD
        let target = anchor.window.frame
        let area = (anchor.window.screen ?? NSScreen.main)?.visibleFrame ?? target
        var x = target.midX - contentSize.width / 2
        // Under it; above when there is no room below (satellite parked at the bottom of the display).
        let gap: CGFloat = anchor.isHUD ? -18 : 2
        above = target.minY - gap - 150 < area.minY
        var top = above ? target.maxY + gap + contentSize.height : target.minY - gap
        if anchor.isHUD, let offset = hudOffset {
            x = target.minX + offset.x
            top = target.minY + offset.y
            above = false
        }
        x = min(max(x, area.minX + 4), area.maxX - contentSize.width - 4)
        top = min(max(top, area.minY + contentSize.height + 4), area.maxY - 2)
        panel.setFrame(NSRect(x: x, y: top - contentSize.height, width: contentSize.width, height: contentSize.height), display: true)
        model.above = above
    }

    /// The content changed height (answer shown, longer text): the edge towards the anchor stays still.
    private func resized(_ size: NSSize) {
        let size = NSSize(width: Self.width, height: ceil(size.height))
        guard size != contentSize, size.height > 10 else { return }
        guard let panel, panel.isVisible, !dragging else { contentSize = size; return }
        let old = panel.frame
        contentSize = size
        // Dragged or not, it grows away from the anchor: keep the top edge (below) or the bottom edge (above).
        let y = above ? old.minY : old.maxY - size.height
        var frame = NSRect(x: old.minX, y: y, width: size.width, height: size.height)
        if let area = (panel.screen ?? NSScreen.main)?.visibleFrame {
            frame.origin.y = min(max(frame.origin.y, area.minY + 4), area.maxY - frame.height - 2)
        }
        panel.setFrame(frame, display: true)
    }

    // MARK: Drag (only next to the sphere's window: under the satellite it stays hooked)

    enum DragPhase { case moved, ended, reset }
    private var dragging = false
    private var dragStart: (mouse: NSPoint, origin: NSPoint)?

    private func dragged(_ phase: DragPhase) {
        guard let panel, isHUD, let target = followed?.frame else { return }
        switch phase {
        case .moved:
            let mouse = NSEvent.mouseLocation
            let start = dragStart ?? (mouse, panel.frame.origin)
            dragStart = start
            dragging = true
            panel.setFrameOrigin(NSPoint(x: start.origin.x + mouse.x - start.mouse.x, y: start.origin.y + mouse.y - start.mouse.y))
        case .ended:
            dragStart = nil
            dragging = false
            hudOffset = NSPoint(x: panel.frame.minX - target.minX, y: panel.frame.maxY - target.minY)
            place()
        case .reset:
            hudOffset = nil
            place()
        }
        touch()
    }

    // MARK: Leaves by itself

    /// Every use restarts the wait. After 45 s without use it leaves, unless something is still
    /// written in it or JARVIS is still working on the answer.
    private func touch() {
        guard isVisible else { return }
        idle?.invalidate()
        idle = Timer.scheduledTimer(withTimeInterval: 45, repeats: false) { [weak self] _ in
            Task { @MainActor in
                guard let self else { return }
                if self.model.draft.isEmpty, self.assistant.state == .idle { self.hide() } else { self.touch() }
            }
        }
    }
}

private extension Assistant.State {
    var wireStateIsIdle: Bool { self == .idle }
}

/// Borderless panel that takes the keyboard without bringing JARVIS's other windows forward.
final class TypeBarPanel: NSPanel {
    var onCancel: (() -> Void)?
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { false }
    override func cancelOperation(_ sender: Any?) { onCancel?() }
}

@MainActor
final class TypeBarModel: ObservableObject {
    @Published var draft = ""
    /// Something was sent since the bar opened: the answer is shown.
    @Published var asked = false
    /// The bar sits above its anchor: the answer goes over the field instead of under it.
    @Published var above = false
    /// Bumped at every opening: the field takes the keyboard again.
    @Published var focus = 0
}

struct TypeBarView: View {
    @ObservedObject var assistant: Assistant
    @ObservedObject var model: TypeBarModel
    var onSend: (String) -> Bool
    var onActivity: () -> Void
    var onSize: (NSSize) -> Void
    var onDrag: (TypeBar.DragPhase) -> Void
    var onClose: () -> Void
    @FocusState private var typing: Bool

    private var exchange: Assistant.Exchange? { model.asked ? assistant.conversation.last : nil }

    var body: some View {
        VStack(spacing: 6) {
            if model.above { answer }
            field
            if !model.above { answer }
        }
        .frame(width: TypeBar.width)
        .fixedSize(horizontal: false, vertical: true)
        .background(GeometryReader { proxy in
            Color.clear.preference(key: TypeBarHeight.self, value: proxy.size.height)
        })
        .onPreferenceChange(TypeBarHeight.self) { height in
            onSize(NSSize(width: TypeBar.width, height: height))
        }
        .onChange(of: model.focus) { _, _ in typing = true }
        .onAppear { typing = true }
    }

    private var field: some View {
        HStack(spacing: 8) {
            Image(systemName: "keyboard")
                .font(.system(size: 13))
                .foregroundStyle(HUD.cyan.opacity(0.8))
                .frame(width: 22, height: 26)
                .contentShape(Rectangle())
                .gesture(DragGesture(minimumDistance: 2, coordinateSpace: .global)
                    .onChanged { _ in onDrag(.moved) }
                    .onEnded { _ in onDrag(.ended) })
                .onTapGesture(count: 2) { onDrag(.reset) }
                .help("Trascina per spostare la barra (accanto alla sfera) · doppio clic la rimette al suo posto")
            TextField("Scrivi a JARVIS", text: $model.draft, axis: .vertical)
                .textFieldStyle(.plain)
                .lineLimit(1...4)
                .font(.system(size: 13))
                .foregroundStyle(.white)
                .tint(HUD.cyan)
                .focused($typing)
                .onSubmit(send)
                .onChange(of: model.draft) { _, _ in onActivity() }
            Button {
                assistant.typedVoice.toggle()
                onActivity()
            } label: {
                Image(systemName: assistant.typedVoice ? "speaker.wave.2.fill" : "speaker.slash")
                    .font(.system(size: 12))
                    .foregroundStyle(assistant.typedVoice ? HUD.cyan : HUD.cyan.opacity(0.45))
                    .frame(width: 22, height: 26)
                    .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .help(assistant.typedVoice ? "JARVIS legge la risposta anche a voce · clic per solo scritto"
                                       : "Risposta solo scritta · clic per sentirla anche a voce")
        }
        .padding(.horizontal, 10)
        .padding(.vertical, 5)
        .background(Capsule(style: .continuous).fill(HUD.background.opacity(0.94)))
        .overlay(Capsule(style: .continuous).stroke(HUD.cyan.opacity(typing ? 0.85 : 0.45), lineWidth: 1))
        .padding(1)
    }

    @ViewBuilder private var answer: some View {
        if let exchange {
            let waiting = exchange.reply.isEmpty
            ScrollView {
                Text(waiting ? Self.waitingText(assistant.state) : exchange.reply)
                    .font(.system(size: 12.5))
                    .foregroundStyle(waiting ? HUD.cyan.opacity(0.7) : Color.white.opacity(0.94))
                    .textSelection(.enabled)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .padding(.horizontal, 12)
                    .padding(.vertical, 8)
            }
            .frame(maxHeight: 220)
            .fixedSize(horizontal: false, vertical: true)
            .background(RoundedRectangle(cornerRadius: 12, style: .continuous).fill(HUD.background.opacity(0.94)))
            .overlay(RoundedRectangle(cornerRadius: 12, style: .continuous).stroke(HUD.deep.opacity(0.7), lineWidth: 1))
            .padding(1)
        }
    }

    private static func waitingText(_ state: Assistant.State) -> String {
        switch state {
        case .acting: return "Eseguo…"
        case .idle: return "Fatto."
        default: return "Ci penso…"
        }
    }

    private func send() {
        let text = model.draft
        guard !text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return }
        if onSend(text) { model.draft = "" }
        typing = true
    }
}

private struct TypeBarHeight: PreferenceKey {
    static let defaultValue: CGFloat = 0
    static func reduce(value: inout CGFloat, nextValue: () -> CGFloat) { value = max(value, nextValue()) }
}
