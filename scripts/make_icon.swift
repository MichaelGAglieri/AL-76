// Draws the app icon and saves it as a 1024×1024 PNG: the neural sphere of the HUD on a vivid blue tile
// (16 November, "Elettrico": the user did not like the near-black tile).
// scripts/icon_sphere.png is the sphere rendered by the HUD's own Metal renderer on a transparent
// background (idle, 1600×1600), so the tile colour shows through between the links.
// Usage: swift scripts/make_icon.swift <output.png> [ios]
// "ios": a full square with no margins or shadow (iOS rounds the corners itself), for the iPhone and iPad app.
import AppKit

let size: CGFloat = 1024
let output = CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "icon_1024.png"
let ios = CommandLine.arguments.count > 2 && CommandLine.arguments[2] == "ios"
let source = URL(fileURLWithPath: #filePath).deletingLastPathComponent().appendingPathComponent("icon_sphere.png")
guard let sphere = NSImage(contentsOf: source) else { fatalError("Manca \(source.path)") }
let space = CGColorSpaceCreateDeviceRGB()
func rgb(_ r: Double, _ g: Double, _ b: Double) -> CGColor { CGColor(red: r / 255, green: g / 255, blue: b / 255, alpha: 1) }

let image = NSImage(size: NSSize(width: size, height: size))
image.lockFocus()
let ctx = NSGraphicsContext.current!.cgContext
let tile = ios ? CGPath(rect: CGRect(x: 0, y: 0, width: size, height: size), transform: nil)
    : CGPath(roundedRect: CGRect(x: 100, y: 100, width: 824, height: 824), cornerWidth: 185, cornerHeight: 185, transform: nil)
ctx.saveGState()
if !ios { ctx.setShadow(offset: CGSize(width: 0, height: -12), blur: 28, color: CGColor(gray: 0, alpha: 0.45)) }
ctx.addPath(tile); ctx.setFillColor(rgb(8, 30, 84)); ctx.fillPath()
ctx.restoreGState()
ctx.addPath(tile); ctx.clip()
// Vivid blue, brighter in the middle behind the sphere.
let centre = CGPoint(x: 512, y: 512)
let blue = CGGradient(colorsSpace: space, colors: [rgb(30, 130, 220), rgb(14, 60, 140), rgb(8, 30, 84)] as CFArray,
                      locations: [0, 0.6, 1])!
ctx.drawRadialGradient(blue, startCenter: centre, startRadius: 0, endCenter: centre, endRadius: ios ? 720 : 600,
                       options: [.drawsAfterEndLocation])

// The sphere, plus a lighter pass that makes the links glow on the blue.
let side: CGFloat = ios ? 960 : 790
let rect = NSRect(x: 512 - side / 2, y: 512 - side / 2, width: side, height: side)
sphere.draw(in: rect, from: .zero, operation: .sourceOver, fraction: 1)
sphere.draw(in: rect, from: .zero, operation: .plusLighter, fraction: 0.35)

image.unlockFocus()
let out = NSBitmapImageRep(data: image.tiffRepresentation!)!
try! out.representation(using: .png, properties: [:])!.write(to: URL(fileURLWithPath: output))
