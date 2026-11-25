import AppKit

/// The app icon chosen in Impostazioni › Aspetto (1.30.86, user request): "robottino" (the bundle's own,
/// AppIcon.icns), "rete" (the neural sphere) or "reattore" (the historical one). build.sh draws all three
/// from scripts/icone and puts them in Resources as Icona-<id>.icns.
///
/// The Dock takes the choice at once (applicationIconImage). Finder, Spotlight and notifications read the
/// bundle, so a choice other than the robottino also goes on the bundle as a custom icon (NSWorkspace.setIcon:
/// an "Icon\r" file and a Finder flag). The signature stays valid on disk and satisfies its designated
/// requirement, so the permissions stay (checked on a copy, 25 November; only `codesign --strict` objects).
/// Every install replaces the bundle, so apply() at launch puts the custom icon back.
@MainActor
enum AppIconChoice {
    static let all: [(id: String, title: String)] = [("robottino", "Robottino"), ("rete", "Rete neurale"), ("reattore", "Reattore")]
    static let standard = "robottino"

    private static var cache: [String: NSImage] = [:]

    static func image(_ id: String) -> NSImage? {
        if let image = cache[id] { return image }
        let image = Bundle.main.url(forResource: "Icona-\(id)", withExtension: "icns").flatMap(NSImage.init(contentsOf:))
        cache[id] = image
        return image
    }

    static func apply(_ id: String) {
        let choice = all.contains { $0.id == id } ? id : standard
        NSApp.applicationIconImage = choice == standard ? nil : image(choice)
        let bundle = Bundle.main.bundleURL
        // `swift run` has no bundle to dress.
        guard bundle.pathExtension == "app" else { return }
        let hasCustom = FileManager.default.fileExists(atPath: bundle.appendingPathComponent("Icon\r").path)
        let onBundle = UserDefaults.standard.string(forKey: "bundleIcon")
        if choice == standard {
            guard hasCustom else { return }
            NSWorkspace.shared.setIcon(nil, forFile: bundle.path, options: [])
        } else {
            guard !hasCustom || onBundle != choice, let icon = image(choice) else { return }
            guard NSWorkspace.shared.setIcon(icon, forFile: bundle.path, options: []) else {
                Diagnostics.log("icona  non riesco a metterla sul pacchetto: \(choice)")
                return
            }
        }
        UserDefaults.standard.set(choice, forKey: "bundleIcon")
        Diagnostics.log("icona  \(choice) sul pacchetto")
    }
}
