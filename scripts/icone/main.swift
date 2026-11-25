// Draws one of AL-76's three app icons (chosen in Impostazioni › Aspetto) as a 1024×1024 PNG.
// Usage: make_icon <rete|reattore|robottino> <output.png> [ios]
// "ios": a full square with no margins or shadow (iOS rounds the corners itself), for the iPhone and iPad app.
// build.sh compiles this folder: swiftc -O scripts/icone/*.swift -o build/make_icon
import AppKit

let args = CommandLine.arguments
guard args.count > 2, ["rete", "reattore", "robottino"].contains(args[1]) else {
    FileHandle.standardError.write("uso: make_icon <rete|reattore|robottino> <uscita.png> [ios]\n".data(using: .utf8)!)
    exit(2)
}
let ios = args.count > 3 && args[3] == "ios"
let size = 1024
let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8, samplesPerPixel: 4,
                           hasAlpha: true, isPlanar: false, colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
NSGraphicsContext.saveGraphicsState()
NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
let ctx = NSGraphicsContext.current!.cgContext
switch args[1] {
case "rete": ReteIcon.draw(in: ctx, ios: ios)
case "reattore": ReattoreIcon.draw(in: ctx, ios: ios)
default: RobottinoIcon.draw(in: ctx, ios: ios)
}
NSGraphicsContext.restoreGraphicsState()
try! rep.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: args[2]))
