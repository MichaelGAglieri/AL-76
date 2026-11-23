import AppKit
import Combine
import SwiftUI

/// Impostazioni › Permessi (v1.30.74, user request 2026-11-17): everything AL-76 is allowed to do, in one place.
/// Top: the macOS permissions with their live state; switching one on asks macOS (or opens the right page of
/// System Settings when macOS has already been answered), switching one off opens System Settings, because
/// only the user can take a permission back there. Below: AL-76's own abilities, saved with «Salva».
struct PermissionsPage: View {
    @Binding var draft: JarvisConfig
    @State private var granted: Set<Permission> = []
    @State private var fullDisk = false
    private let timer = Timer.publish(every: 1, on: .main, in: .common).autoconnect()

    var body: some View {
        Group {
            Section {
                ForEach(Permission.allCases) { permission in
                    row(icon: permission.icon, title: permission.name, text: permission.why,
                        isOn: Binding(get: { granted.contains(permission) },
                                      set: { on in
                                          if on { Task { await permission.request(); refresh() } } else { permission.openSettings() }
                                      }))
                }
                row(icon: "internaldrive", title: "Accesso completo al disco",
                    text: "Facoltativo: rende più veloce la ricerca nella posta e nei file protetti.",
                    isOn: Binding(get: { fullDisk }, set: { _ in Self.openSystemSettings("Privacy_AllFiles") }))
                HStack(alignment: .top, spacing: 12) {
                    icon("gearshape.2")
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Automazione").font(.body.weight(.medium))
                        Caption("Mail, Musica, Finder e le altre app: macOS chiede il permesso di comandarle la prima volta che serve. Qui vedi e togli quelli già dati.")
                    }
                    Spacer()
                    Button("Apri Impostazioni") { Self.openSystemSettings("Privacy_Automation") }
                }
                .padding(.vertical, 2)
            } header: {
                Text("Permessi di macOS")
            } footer: {
                Caption("Lo stato si aggiorna da solo. Un permesso di macOS si toglie solo in Impostazioni di Sistema › Privacy e sicurezza: spegnendo l'interruttore si apre la pagina giusta.")
            }

            ForEach(Ability.groups, id: \.self) { group in
                Section(group) {
                    ForEach(Ability.allCases.filter { $0.group == group }) { ability in
                        abilityRow(ability)
                    }
                }
            }
            Section {
                Caption("Questi interruttori valgono per ogni cervello (locale, Claude, ChatGPT, Gemini), per la voce, la barra di scrittura e l'iPhone, dopo «Salva». Fermare il robot, leggere lo stato della stampante e i timer funzionano sempre. Ogni rifiuto finisce nel registro di sicurezza.")
            }
        }
        .onAppear(perform: refresh)
        .onReceive(timer) { _ in refresh() }
    }

    @ViewBuilder private func abilityRow(_ ability: Ability) -> some View {
        let allowed = draft.allows(ability)
        let missing = Self.needs(ability).filter { !granted.contains($0) }
        VStack(alignment: .leading, spacing: 4) {
            row(icon: ability.icon, title: ability.title, text: ability.explanation,
                isOn: Binding(get: { draft.allows(ability) },
                              set: { on in
                                  draft.set(ability, allowed: on)
                                  // Switched on without the macOS permission behind it: ask for it now, like professional apps do.
                                  if on, let first = Self.needs(ability).first(where: { !granted.contains($0) }) {
                                      Task { await first.request(); refresh() }
                                  }
                              }))
            if allowed, !missing.isEmpty {
                HStack(spacing: 8) {
                    Label("Serve il permesso di macOS «\(missing.map(\.name).joined(separator: "», «"))»", systemImage: "exclamationmark.triangle")
                        .font(.caption)
                        .foregroundStyle(HUD.amber)
                    Button("Consenti") { Task { for permission in missing { await permission.request() }; refresh() } }
                        .controlSize(.small)
                }
                .padding(.leading, 38)
            }
        }
    }

    private func row(icon name: String, title: String, text: String, isOn: Binding<Bool>) -> some View {
        // The whole row is the switch's label, like the other pages: VoiceOver reads the title, and a click works.
        Toggle(isOn: isOn) {
            HStack(alignment: .top, spacing: 12) {
                icon(name)
                VStack(alignment: .leading, spacing: 2) {
                    Text(title).font(.body.weight(.medium))
                    Caption(text)
                }
            }
        }
        .toggleStyle(.switch)
        .padding(.vertical, 2)
    }

    private func icon(_ name: String) -> some View {
        Image(systemName: name)
            .font(.title3)
            .foregroundStyle(HUD.cyan)
            .frame(width: 26)
    }

    private func refresh() {
        granted = Set(Permission.allCases.filter(\.granted))
        fullDisk = Self.hasFullDiskAccess
    }

    /// The macOS permissions an ability cannot work without.
    static func needs(_ ability: Ability) -> [Permission] {
        switch ability {
        case .appControl: return [.accessibility]
        case .screen: return [.screen]
        case .camera: return [.camera]
        case .calendar: return [.calendar, .reminders]
        default: return []
        }
    }

    /// Without Full Disk Access the Mail folder cannot be listed.
    static var hasFullDiskAccess: Bool {
        let mail = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Mail").path
        return (try? FileManager.default.contentsOfDirectory(atPath: mail)) != nil
    }

    static func openSystemSettings(_ anchor: String) {
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?\(anchor)") {
            NSWorkspace.shared.open(url)
        }
    }
}
