// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "JARVIS",
    platforms: [.macOS(.v15)],
    dependencies: [
        .package(url: "https://github.com/argmaxinc/WhisperKit.git", exact: "1.1.1"),
    ],
    targets: [
        .executableTarget(
            name: "JARVIS",
            dependencies: [.product(name: "WhisperKit", package: "WhisperKit")],
            path: "Sources/JARVIS",
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
        // `swift test` (or scripts/test.sh): the command parsers and the wake word checks, on fixed phrases
        // and, on this Mac only, on the phrases really heard (voce.log, diagnostica.log). Since 1.30.83.
        .testTarget(
            name: "JARVISTests",
            dependencies: ["JARVIS"],
            path: "tests/unit",
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
    ]
)
