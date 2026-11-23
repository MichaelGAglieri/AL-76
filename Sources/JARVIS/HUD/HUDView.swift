import SwiftUI

/// Main screen: only the reactor and its state, with no buttons around it. The Plancia, the
/// SATELLITE and the settings are in the quick menu (menu-bar icon, right click); Esc hides it.
/// A double click on the sphere goes back to the SATELLITE.
struct HUDView: View {
    /// The window wraps the sphere closely (11 November): the drawn sphere is about 400 points
    /// across inside its 490-point frame, and up to 7% larger while JARVIS speaks.
    static let size = CGSize(width: 440, height: 472)
    static let reactorSize: CGFloat = 490
    /// The sphere's centre, from the top of the window; the state label sits under it.
    static let reactorCenterFromTop: CGFloat = 220

    @ObservedObject var assistant: Assistant
    var onCompact: () -> Void = {}
    /// Reactor drag in progress; `true` when it ends.
    var onWindowDrag: (Bool) -> Void = { _ in }

    var body: some View {
        ZStack(alignment: .topLeading) {
            NucleusHalo()
                .frame(width: Self.size.width, height: Self.size.height)
            ReactorView(assistant: assistant, paused: !assistant.hudVisible)
                .frame(width: Self.reactorSize, height: Self.reactorSize)
                .position(x: Self.size.width / 2, y: Self.reactorCenterFromTop)
            stateLabel
                .position(x: Self.size.width / 2, y: Self.size.height - 26)
        }
        .frame(width: Self.size.width, height: Self.size.height)
        // The window moves by dragging the sphere; a double click reduces it to the SATELLITE.
        .contentShape(Rectangle())
        .onTapGesture(count: 2) { onCompact() }
        // The one quick menu (1.30.81), the same as the robot's, the satellite's and the menu-bar icon's:
        // right click or two-finger tap on the sphere. Every other click goes through to the sphere.
        .overlay(QuickMenuArea())
        .gesture(DragGesture(minimumDistance: 3)
            .onChanged { _ in onWindowDrag(false) }
            .onEnded { _ in onWindowDrag(true) })
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .ignoresSafeArea()
    }

    private var stateLabel: some View {
        let label = assistant.privacyMode ? "PRIVACY · ⌃⌥P PER RIATTIVARE"
            : (assistant.language == .italian ? assistant.state.label.it : assistant.state.label.en)
        // The everyday states (waiting, working, answering) are not written: the sphere shows them
        // (11 November, user request). Only what needs the user's attention stays: privacy and errors.
        let shown = assistant.privacyMode || assistant.state.isError
        return VStack(spacing: 4) {
            if shown {
                Text(label)
                    .padding(.horizontal, 12).padding(.vertical, 3)
                    .background(Capsule().fill(HUD.background.opacity(0.55)))
                    .font(HUD.font(13, .bold)).tracking(5)
                    .foregroundStyle(assistant.state.isError ? HUD.amber : HUD.ice)
                    .shadow(color: HUD.cyan, radius: 6)
            }
            if case .error(let detail) = assistant.state {
                // The cause of the error, under the label: you know at once what is wrong.
                Text(detail).font(HUD.font(10)).foregroundStyle(HUD.amber.opacity(0.9))
                    .multilineTextAlignment(.center).lineLimit(2).frame(maxWidth: 440)
            }
            if assistant.config.developerMode {
                Text("MODALITÀ TEST ATTIVA").font(HUD.font(9)).tracking(2).foregroundStyle(HUD.amber)
            }
        }
        .animation(.easeInOut(duration: 0.25), value: label)
    }

}

// MARK: Components

/// Dark translucent halo around the reactor: it fades into the desktop, without borders,
/// so the window looks like an enlarged NUCLEUS.
struct NucleusHalo: View {
    var body: some View {
        RadialGradient(stops: [.init(color: HUD.background.opacity(0.62), location: 0),
                               .init(color: HUD.background.opacity(0.5), location: 0.72),
                               .init(color: .clear, location: 1)],
                       center: UnitPoint(x: 0.5, y: HUDView.reactorCenterFromTop / HUDView.size.height), startRadius: 0, endRadius: 225)
    }
}

/// Hosts the view that catches the right click for the quick menu.
struct QuickMenuArea: NSViewRepresentable {
    func makeNSView(context: Context) -> QuickMenuCatcher { QuickMenuCatcher() }
    func updateNSView(_ nsView: QuickMenuCatcher, context: Context) {}
}
