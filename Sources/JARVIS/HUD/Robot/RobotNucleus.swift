import AppKit
import Combine
import SceneKit

/// The ROBOTTINO inside a NUCLEUS window. It follows JARVIS's state and the real voice level, walks on the
/// bottom edge of the display when it has nothing to do (if the user wants it), answers typed questions in a
/// speech bubble from its mouth, and catches the mouse only on the model itself: around it, clicks go through
/// to the windows below. Click: a pat. Double click: the HUD. Drag: moves it. Right click: the usual menu.
@MainActor
final class RobotNucleusView: NSView {
    /// Where the model is: the user's own file, never part of JARVIS.
    static var modelURL: URL { JarvisConfig.directory.appendingPathComponent("Companion/scene.gltf") }
    static var modelAvailable: Bool { FileManager.default.fileExists(atPath: modelURL.path) }

    /// Window size: the small one (chosen 16 November) and the large one.
    static func size(large: Bool) -> NSSize { large ? NSSize(width: 200, height: 330) : NSSize(width: 160, height: 264) }

    var onOpen: (() -> Void)?
    private weak var assistant: Assistant?
    private let scnView = RobotSCNView()
    private var robot: RobotKit.Robot?
    private let bubble = RobotKit.Bubble()
    private var watchers: Set<AnyCancellable> = []
    private var timer: Timer?
    private let start = Date()
    private var last: CGFloat = 0
    private var voice: CGFloat = 0
    /// The answer to show in the bubble: the exchange of the last typed question.
    private var typedID: UUID?

    // Walking on the bottom edge.
    private var target: CGFloat?       // end of the current stroll (window x); nil: going home or at home
    private var strollUntil: CGFloat = 0, nextStroll: CGFloat = 20
    private var exact: NSPoint?        // the window's position with its fractions; the window server keeps whole points
    private var lastOrigin = NSPoint.zero
    private var wasWalking = false

    // Mouse.
    private var press: (mouse: NSPoint, origin: NSPoint)?
    private var dragging = false
    private var lastMouse = NSPoint(x: -1, y: -1)

    init(assistant: Assistant) {
        self.assistant = assistant
        super.init(frame: NSRect(origin: .zero, size: Self.size(large: assistant.config.robotLarge)))
        wantsLayer = true
        layer?.isOpaque = false
        scnView.frame = bounds
        scnView.autoresizingMask = [.width, .height]
        scnView.wantsLayer = true
        scnView.layer?.isOpaque = false
        scnView.backgroundColor = .clear
        scnView.antialiasingMode = .multisampling4X
        scnView.preferredFramesPerSecond = 30
        addSubview(scnView)
        load()
        assistant.$typedExchange.sink { [weak self] id in self?.typedID = id }.store(in: &watchers)
        assistant.$config.map(\.robotFlames).removeDuplicates().sink { [weak self] flames in
            self?.robot?.flames(flames == "ioniche" ? .ion : .fire)
        }.store(in: &watchers)
    }

    required init?(coder: NSCoder) { fatalError() }

    override var isOpaque: Bool { false }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    /// The model loads off the main thread (textures, 250 thousand triangles), then appears.
    private func load() {
        let url = Self.modelURL
        DispatchQueue.global(qos: .userInitiated).async {
            let made = Result { try RobotKit.makeScene(model: url) }
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                switch made {
                case .success(let (scene, robot, camera)):
                    self.robot = robot
                    robot.flames(self.assistant?.config.robotFlames == "ioniche" ? .ion : .fire)
                    self.scnView.scene = scene
                    self.scnView.pointOfView = camera
                    self.scnView.isPlaying = true
                    Diagnostics.log("robottino caricato")
                case .failure(let error):
                    Diagnostics.log("robottino: modello non caricato (\(error))")
                }
            }
        }
    }

    /// Runs while the window is on screen.
    override func viewDidMoveToWindow() {
        super.viewDidMoveToWindow()
        timer?.invalidate()
        timer = nil
        guard window != nil else {
            bubble.hide()
            return
        }
        let t = Timer(timeInterval: 1.0 / 30, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.tick() }
        }
        RunLoop.main.add(t, forMode: .common)   // keeps moving while it is dragged
        timer = t
    }

    private var panel: ReactorPanel? { window as? ReactorPanel }

    // MARK: - Every frame

    private func tick() {
        guard let assistant, let panel, let robot else { return }
        let t = CGFloat(Date().timeIntervalSince(start))
        let dt = min(0.1, max(0, t - last))
        last = t
        let shown = panel.isVisible && panel.alphaValue > 0.01 && assistant.compactVisible
        scnView.isPlaying = shown
        scnView.preferredFramesPerSecond = assistant.state == .idle && !(robot.walk != 0) ? 24 : 30
        guard shown else {
            bubble.hide()
            return
        }

        // JARVIS's state.
        let mood: RobotKit.Mood
        switch assistant.state {
        case .listening: mood = .listen
        case .transcribing, .thinking, .acting, .booting: mood = .think
        case .speaking: mood = .speak
        case .error: mood = .error
        // Waiting it stays awake (user request, 16 November): no sleep in JARVIS's deep idle.
        case .idle: mood = bubble.typing ? .speak : .idle
        }
        let raw = CGFloat(min(1, max(0, (assistant.levels.voice) * 1.8)))
        voice += (raw - voice) * (1 - exp(-dt * 18))
        let level = mood == .speak ? (assistant.state == .speaking ? voice : RobotKit.voiceLevel(t)) : 0
        robot.face.privacy = assistant.privacyMode

        // On the bottom edge (if the user wants it): it lives in the bottom right corner and now and then
        // takes a stroll along the edge and comes back, only while JARVIS waits (16 November).
        if assistant.config.robotWalks, !dragging, let screen = panel.ownScreen?.visibleFrame {
            var f = panel.frame
            if let e = exact, abs(e.x - f.origin.x) < 2, abs(e.y - f.origin.y) < 2 { f.origin = e }   // else it was moved
            let ground = screen.minY - feetFraction * f.height + 1
            let home = screen.maxX - f.width * 0.78
            if abs(f.origin.y - ground) > 1.5 {
                // Flying down to the edge, engines on.
                robot.grounded = false
                robot.walk = 0
                f.origin.y += (ground - f.origin.y) * min(1, dt * 3)
                if abs(f.origin.y - ground) < 1.5 { f.origin.y = ground }
                strollUntil = 0
                nextStroll = t + 4
            } else {
                robot.grounded = true
                f.origin.y = ground
                // Where it is going: home, or the end of a stroll. A stroll starts every minute or so
                // (every few minutes in JARVIS's deep idle) and ends with a look around before coming back.
                if mood == .idle {
                    if target == nil, t > nextStroll {
                        let reach = screen.width * CGFloat.random(in: 0.18...0.45)
                        target = max(screen.minX + 10, home - reach)
                        strollUntil = 0
                    }
                } else {
                    target = nil   // busy: it stops where it is and acts the state
                }
                let goal = target ?? home
                let distance = goal - f.origin.x
                if mood != .idle || t < strollUntil {
                    robot.walk = 0
                } else if abs(distance) < 2 {
                    robot.walk = 0
                    if target != nil {
                        // At the end of the stroll: a look around, then home.
                        strollUntil = t + CGFloat.random(in: 2.5...4)
                        target = nil
                    } else if nextStroll < t {
                        nextStroll = t + (assistant.deepIdle ? CGFloat.random(in: 150...300) : CGFloat.random(in: 50...90))
                    }
                } else {
                    robot.walk = distance > 0 ? 1 : -1
                    // Window speed from the stride, so the planted foot does not slide.
                    let p0 = scnView.projectPoint(SCNVector3(0, 0, 0)), p1 = scnView.projectPoint(SCNVector3(1, 0, 0))
                    let step = RobotKit.Robot.walkSpeed * robot.walkAmount * abs(sin(robot.facing)) * abs(p1.x - p0.x) * dt
                    f.origin.x += min(abs(distance), step) * robot.walk
                }
            }
            exact = f.origin
            if f.origin != panel.frame.origin { panel.setFrameOrigin(f.origin) }
            wasWalking = true
        } else if wasWalking, !assistant.config.robotWalks {
            // Walking turned off: engines on, and up a little from the edge.
            wasWalking = false
            robot.grounded = false
            robot.walk = 0
            exact = nil
            target = nil
            panel.move(to: NSPoint(x: panel.frame.minX, y: panel.frame.minY + panel.frame.height * 0.3))
        } else {
            exact = nil
        }
        let dx = panel.frame.origin.x - lastOrigin.x
        lastOrigin = panel.frame.origin
        robot.lean = robot.grounded ? 0 : max(-0.4, min(0.4, dx * 0.04))
        robot.update(t: t, mood: mood, level: level)

        // The answer to a typed question comes out of its mouth.
        if let id = typedID, let exchange = assistant.conversation.last, exchange.id == id, !exchange.reply.isEmpty {
            bubble.show(exchange.reply, id: id, at: t)
        }
        if bubble.panel.isVisible, let screen = panel.ownScreen?.visibleFrame {
            let m = scnView.projectPoint(robot.mouthMark.presentation.worldPosition)
            let mouth = toScreen(m)
            let xs = robot.headOutline().map { toScreen(scnView.projectPoint($0)).x }
            _ = bubble.update(t: t, mouth: mouth, left: xs.min() ?? mouth.x, right: xs.max() ?? mouth.x, screen: screen)
        }

        updateMouseCatching(panel)
    }

    private func toScreen(_ p: SCNVector3) -> NSPoint {
        guard let window else { return .zero }
        return window.convertPoint(toScreen: scnView.convert(NSPoint(x: p.x, y: p.y), to: nil))
    }

    /// The feet, as a fraction of the window's height from the bottom.
    private var feetFraction: CGFloat {
        let p = scnView.projectPoint(SCNVector3(0, 0, 0))
        let f = p.y / max(1, scnView.bounds.height)
        return f > 0.5 ? 1 - f : f
    }

    /// Only the model catches the mouse: anywhere else in the window, clicks go to what is below.
    private func updateMouseCatching(_ panel: ReactorPanel) {
        let mouse = NSEvent.mouseLocation
        guard mouse != lastMouse || !panel.ignoresMouseEvents else { return }
        lastMouse = mouse
        if panel.isCoveredByOthers { panel.ignoresMouseEvents = true; return }
        if dragging || press != nil { panel.ignoresMouseEvents = false; return }
        guard panel.frame.contains(mouse) else { panel.ignoresMouseEvents = true; return }
        let local = scnView.convert(panel.convertPoint(fromScreen: mouse), from: nil)
        let hits = scnView.hitTest(local, options: [.categoryBitMask: 2, .searchMode: SCNHitTestSearchMode.any.rawValue])
        panel.ignoresMouseEvents = hits.isEmpty
    }

    // MARK: - Mouse

    /// Recent mouse positions while dragging, to tell a throw from a drop.
    private var trail: [(time: TimeInterval, point: NSPoint)] = []

    override func mouseDown(with event: NSEvent) {
        press = window.map { (NSEvent.mouseLocation, $0.frame.origin) }
        dragging = false
        trail = []
        if event.clickCount == 2 { press = nil; onOpen?() }
    }

    override func mouseDragged(with event: NSEvent) {
        guard let press, let panel else { return }
        let mouse = NSEvent.mouseLocation
        let dx = mouse.x - press.mouse.x, dy = mouse.y - press.mouse.y
        guard dragging || abs(dx) > 3 || abs(dy) > 3 else { return }
        dragging = true
        trail.append((event.timestamp, mouse))
        if trail.count > 8 { trail.removeFirst() }
        // Free movement: on the edge the window hangs partly below the display.
        panel.setFrameOrigin(NSPoint(x: press.origin.x + dx, y: press.origin.y + dy))
    }

    override func mouseUp(with event: NSEvent) {
        if !dragging, press != nil, event.clickCount == 1 {
            // A pat: it lights up and waves.
            robot?.poke = CGFloat(Date().timeIntervalSince(start))
        } else if dragging, let panel, let assistant, let screen = panel.ownScreen?.visibleFrame {
            // The gesture: dropped or thrown towards the bottom right, it goes back to the edge and walks;
            // pulled anywhere else, it stays where it is left, floating.
            var velocity = NSPoint.zero
            if let first = trail.first, let last = trail.last, last.time - first.time > 0.01 {
                velocity = NSPoint(x: (last.point.x - first.point.x) / (last.time - first.time),
                                   y: (last.point.y - first.point.y) / (last.time - first.time))
            }
            let f = panel.frame
            let low = f.midY < screen.minY + screen.height * 0.3
            let right = f.midX > screen.minX + screen.width * 0.55
            let thrown = velocity.y < -700 && velocity.x > -200
            let walks = (low && right) || thrown
            if walks != assistant.config.robotWalks { assistant.setRobotWalks(walks) }
            if !walks { panel.move(to: f.origin) }   // back inside the display
            exact = nil
            Diagnostics.log("robottino \(walks ? "sul bordo" : "libero") (velocità \(Int(velocity.x)), \(Int(velocity.y)))")
        }
        press = nil
        dragging = false
    }

    override func rightMouseDown(with event: NSEvent) {
        let menu = NSMenu()
        menu.autoenablesItems = false
        menu.addItem(withTitle: "Apri HUD", action: #selector(openHUD), keyEquivalent: "").target = self
        menu.addItem(withTitle: "Parla (⌃⌥Spazio)", action: #selector(talk), keyEquivalent: "").target = self
        menu.addItem(withTitle: "Scrivi (⌃⌥T)", action: #selector(write), keyEquivalent: "").target = self
        menu.addItem(withTitle: "Nuova conversazione", action: #selector(reset), keyEquivalent: "").target = self
        menu.addItem(.separator())
        menu.addItem(withTitle: "Esci da AL-76", action: #selector(quit), keyEquivalent: "").target = self
        NSMenu.popUpContextMenu(menu, with: event, for: self)
    }

    @objc private func openHUD() { onOpen?() }
    @objc private func talk() { assistant?.pushToTalk() }
    @objc private func write() { NotificationCenter.default.post(name: .jarvisTypeBar, object: nil) }
    @objc private func reset() { assistant?.resetConversation() }
    @objc private func quit() { NSApp.terminate(nil) }
}

/// Mouse events go to the robot's container, not to SceneKit.
final class RobotSCNView: SCNView {
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    override var isOpaque: Bool { false }
}
