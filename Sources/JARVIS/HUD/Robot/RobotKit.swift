// The ROBOTTINO style of the NUCLEUS (16 November): a small 3D robot that floats on jet flames, walks on the
// bottom edge of the display, draws its face on its screen and answers typed questions in a speech bubble.
// The model is not part of JARVIS: it is a glTF file the user puts in Application Support/JARVIS/Companion
// (the one approved is "Mini Robot" by Onur on Sketchfab, CC-BY 4.0). The rig uses that model's part names.
// Built from the preview in tests/companion/robot, where every pose was checked.
import AppKit
import SceneKit

enum RobotKit {
enum Mood: String, CaseIterable {
    case idle = "Attesa", listen = "Ascolta", think = "Pensa", speak = "Parla", happy = "Felice", sleep = "Dorme", error = "Errore"
}

enum FlameStyle { case fire, ion }

static let cyan = NSColor(srgbRed: 0.35, green: 0.88, blue: 1.0, alpha: 1)

// MARK: - Small images

static func image(_ size: Int, _ draw: (CGContext) -> Void) -> CGImage {
    let ctx = CGContext(data: nil, width: size, height: size, bitsPerComponent: 8, bytesPerRow: 0,
                        space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
    draw(ctx)
    return ctx.makeImage()!
}

static let softDot = image(64) { c in
    let g = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB), colors: [CGColor(gray: 1, alpha: 1), CGColor(gray: 1, alpha: 0.5), CGColor(gray: 1, alpha: 0)] as CFArray,
                       locations: [0, 0.35, 1])!
    c.drawRadialGradient(g, startCenter: CGPoint(x: 32, y: 32), startRadius: 0, endCenter: CGPoint(x: 32, y: 32), endRadius: 32, options: [])
}

static let star = image(64) { c in
    c.setFillColor(CGColor(gray: 1, alpha: 1))
    c.move(to: CGPoint(x: 32, y: 2))
    for i in 1..<8 {
        let a = CGFloat(i) * .pi / 4 + .pi / 2, r: CGFloat = i % 2 == 0 ? 30 : 7
        c.addLine(to: CGPoint(x: 32 + cos(a) * r, y: 32 + sin(a) * r))
    }
    c.closePath()
    c.fillPath()
}

static func studio() -> NSImage {
    let size = NSSize(width: 512, height: 256)
    let img = NSImage(size: size)
    img.lockFocus()
    NSGradient(colors: [NSColor(white: 0.97, alpha: 1), NSColor(srgbRed: 0.5, green: 0.56, blue: 0.66, alpha: 1),
                        NSColor(srgbRed: 0.08, green: 0.09, blue: 0.13, alpha: 1)])!.draw(in: NSRect(origin: .zero, size: size), angle: -90)
    NSColor(white: 1, alpha: 0.95).setFill()
    NSRect(x: 150, y: 175, width: 80, height: 40).fill()
    img.unlockFocus()
    return img
}

static func glowMaterial(_ contents: Any, _ color: NSColor = .white) -> SCNMaterial {
    let m = SCNMaterial()
    m.lightingModel = .constant
    m.diffuse.contents = contents
    m.multiply.contents = color
    m.blendMode = .add
    m.writesToDepthBuffer = false
    m.isDoubleSided = true
    return m
}

// MARK: - Geometry helpers

/// Splits a mesh that holds both sides of the body (the arms, the legs, the ears) into a -x half and a +x half,
/// by the world x of each triangle's centre, so each side can move on its own.
static func splitByX(_ holder: SCNNode) -> (SCNNode, SCNNode) {
    let g = holder.geometry!
    let vs = g.sources(for: .vertex)[0]
    let positions: [SIMD3<Float>] = vs.data.withUnsafeBytes { raw in
        (0..<vs.vectorCount).map { i in
            let o = vs.dataOffset + i * vs.dataStride
            return SIMD3(raw.load(fromByteOffset: o, as: Float.self), raw.load(fromByteOffset: o + 4, as: Float.self),
                         raw.load(fromByteOffset: o + 8, as: Float.self))
        }
    }
    let e = g.elements[0]
    let indices: [UInt32] = e.data.withUnsafeBytes { raw in
        (0..<e.primitiveCount * 3).map { i in e.bytesPerIndex == 2 ? UInt32(raw.load(fromByteOffset: i * 2, as: UInt16.self)) : raw.load(fromByteOffset: i * 4, as: UInt32.self) }
    }
    let w = holder.worldTransform
    var neg: [UInt32] = [], pos: [UInt32] = []
    for t in stride(from: 0, to: indices.count, by: 3) {
        let c = (positions[Int(indices[t])] + positions[Int(indices[t + 1])] + positions[Int(indices[t + 2])]) / 3
        let x = CGFloat(c.x) * w.m11 + CGFloat(c.y) * w.m21 + CGFloat(c.z) * w.m31 + w.m41
        if x < 0 { neg += indices[t...t + 2] } else { pos += indices[t...t + 2] }
    }
    func half(_ idx: [UInt32]) -> SCNNode {
        let el = SCNGeometryElement(data: idx.withUnsafeBufferPointer { Data(buffer: $0) }, primitiveType: .triangles,
                                    primitiveCount: idx.count / 3, bytesPerIndex: 4)
        let geo = SCNGeometry(sources: g.sources, elements: [el])
        geo.materials = g.materials
        return SCNNode(geometry: geo)
    }
    return (half(neg), half(pos))
}

static func gearPath(teeth: Int, outer: CGFloat, inner: CGFloat, hole: CGFloat) -> NSBezierPath {
    let p = NSBezierPath()
    for i in 0..<(teeth * 4) {
        let a = CGFloat(i) / CGFloat(teeth * 4) * 2 * .pi
        let r = (i % 4 == 1 || i % 4 == 2) ? outer : inner
        let pt = NSPoint(x: cos(a) * r, y: sin(a) * r)
        if i == 0 { p.move(to: pt) } else { p.line(to: pt) }
    }
    p.close()
    p.append(NSBezierPath(ovalIn: NSRect(x: -hole, y: -hole, width: hole * 2, height: hole * 2)).reversed)
    return p
}

// MARK: - Thrusters

final class Thruster {
    let node = SCNNode()
    private let flame = SCNParticleSystem()
    private let glow: SCNNode
    private let size: CGFloat

    init(size: CGFloat) {
        self.size = size
        flame.particleImage = softDot
        flame.blendMode = .additive
        flame.isLightingEnabled = false
        flame.birthRate = 320
        flame.particleLifeSpan = 0.3
        flame.particleLifeSpanVariation = 0.06
        flame.emittingDirection = SCNVector3(0, -1, 0)
        flame.spreadingAngle = 7
        flame.particleVelocity = 1.1 * size
        flame.particleVelocityVariation = 0.25 * size
        flame.emitterShape = SCNSphere(radius: 0.012 * size)
        flame.particleSize = 0.11 * size
        flame.loops = true
        flame.sortingMode = .none
        flame.propertyControllers = [.size: SCNParticlePropertyController(animation: {
            let a = CAKeyframeAnimation()
            a.values = [1.0, 0.72, 0.18]
            a.keyTimes = [0, 0.4, 1]
            return a
        }())]
        node.addParticleSystem(flame)
        let plane = SCNPlane(width: 0.3 * size, height: 0.3 * size)
        glow = SCNNode(geometry: plane)
        glow.constraints = [SCNBillboardConstraint()]
        glow.position = SCNVector3(0, -0.02 * size, 0)
        node.addChildNode(glow)
        style(.fire)
    }

    func style(_ s: FlameStyle) {
        let colors: [NSColor] = s == .fire
            ? [NSColor(srgbRed: 1, green: 0.93, blue: 0.7, alpha: 0.55), NSColor(srgbRed: 1, green: 0.55, blue: 0.12, alpha: 0.4),
               NSColor(srgbRed: 0.9, green: 0.2, blue: 0.05, alpha: 0)]
            : [NSColor(srgbRed: 0.85, green: 1, blue: 1, alpha: 0.55), NSColor(srgbRed: 0.25, green: 0.78, blue: 1, alpha: 0.4),
               NSColor(srgbRed: 0.1, green: 0.25, blue: 1, alpha: 0)]
        let a = CAKeyframeAnimation()
        a.values = colors
        a.keyTimes = [0, 0.35, 1]
        flame.propertyControllers?[.color] = SCNParticlePropertyController(animation: a)
        glow.geometry?.materials = [glowMaterial(softDot, s == .fire ? NSColor(srgbRed: 1, green: 0.7, blue: 0.35, alpha: 1) : cyan)]
    }

    /// power 0...1.5: flame length, density and glow.
    func update(power: CGFloat, flicker: CGFloat) {
        let p = max(0, power) * (0.9 + 0.2 * flicker)
        flame.birthRate = 420 * min(1, p * 1.2)
        flame.particleVelocity = (0.7 + 1.2 * p) * size
        glow.opacity = min(1, p * 0.9)
        glow.scale = SCNVector3(0.7 + 0.4 * p, 0.7 + 0.4 * p, 1)
    }
}

// MARK: - The face on the screen

/// Where the face sits in the model's emission texture: the screen's dark area, turned a quarter turn.
enum FaceMap {
    static let origin = CGPoint(x: 0.33, y: 0.376), width: CGFloat = 0.2, pixels = 256
    static let r: CGFloat = 0.0145   // eye radius, in texture units
    static var transform: SCNMatrix4 {
        SCNMatrix4Mult(SCNMatrix4MakeTranslation(-origin.x, -origin.y, 0), SCNMatrix4MakeScale(1 / width, 1 / width, 1))
    }
}

struct Face {
    var mood: Mood = .idle
    var blink: CGFloat = 1, look = CGPoint.zero, eyeScale: CGFloat = 1, mouth: CGFloat = 0, t: CGFloat = 0
    var happyEyes = false
    var privacy = false
    /// What the face looks like now, rounded: the texture is redrawn only when this changes.
    var key: String {
        String(format: "%@%d%d %.2f %.3f %.3f %.2f %.2f", mood.rawValue, happyEyes ? 1 : 0, privacy ? 1 : 0,
               blink, look.x, look.y, eyeScale, mouth)
    }

    func draw() -> CGImage {
        image(FaceMap.pixels) { c in
            let s = CGFloat(FaceMap.pixels) / FaceMap.width
            c.translateBy(x: CGFloat(FaceMap.pixels) / 2, y: CGFloat(FaceMap.pixels) / 2)
            c.rotate(by: -.pi / 2)
            c.scaleBy(x: s, y: s)
            c.scaleBy(x: -1, y: 1)   // the texture is mirrored across the face
            let r = FaceMap.r
            let error = mood == .error
            let eye = error ? CGColor(srgbRed: 1, green: 0.32, blue: 0.22, alpha: 1)
                : privacy ? CGColor(srgbRed: 0.78, green: 0.6, blue: 1, alpha: 1) : CGColor(srgbRed: 0.86, green: 0.98, blue: 1, alpha: 1)
            c.setFillColor(eye)
            c.setStrokeColor(eye)
            c.setLineCap(.round)
            c.setLineJoin(.round)
            c.setLineWidth(r * 0.55)
            let eyes = [CGPoint(x: -0.0485 + look.x, y: 0.008 + look.y), CGPoint(x: 0.0485 + look.x, y: 0.008 + look.y)]
            let mouthAt = CGPoint(x: look.x * 0.6, y: -0.02 + look.y * 0.5)
            for (i, e) in eyes.enumerated() {
                switch mood {
                case .error:
                    let k = r * 0.8
                    c.move(to: CGPoint(x: e.x - k, y: e.y - k)); c.addLine(to: CGPoint(x: e.x + k, y: e.y + k))
                    c.move(to: CGPoint(x: e.x - k, y: e.y + k)); c.addLine(to: CGPoint(x: e.x + k, y: e.y - k))
                    c.strokePath()
                case .sleep:
                    c.addArc(center: CGPoint(x: e.x, y: e.y + r * 0.2), radius: r * 0.8, startAngle: .pi * 1.15, endAngle: .pi * 1.85, clockwise: false)
                    c.strokePath()
                case _ where happyEyes || mood == .happy:
                    c.addArc(center: CGPoint(x: e.x, y: e.y - r * 0.4), radius: r * 0.85, startAngle: .pi * 0.15, endAngle: .pi * 0.85, clockwise: false)
                    c.strokePath()
                case .think:
                    // Looking up: the lower lid is flat, like a dome.
                    let w = r * 1.12, h = r * 1.3 * max(0.15, blink)
                    c.move(to: CGPoint(x: e.x - w, y: e.y - h * 0.15))
                    c.addCurve(to: CGPoint(x: e.x + w, y: e.y - h * 0.15), control1: CGPoint(x: e.x - w, y: e.y + h * 1.25), control2: CGPoint(x: e.x + w, y: e.y + h * 1.25))
                    c.closePath()
                    c.fillPath()
                default:
                    let h = r * eyeScale * max(0.08, blink)
                    let w = r * eyeScale
                    c.fillEllipse(in: CGRect(x: e.x - w, y: e.y - h, width: w * 2, height: h * 2))
                }
            }
            // Blush when happy.
            if mood == .happy || happyEyes {
                c.setFillColor(CGColor(srgbRed: 1, green: 0.45, blue: 0.6, alpha: 0.55))
                for e in eyes { c.fillEllipse(in: CGRect(x: e.x - r * 0.75, y: e.y - r * 2.0, width: r * 1.5, height: r * 0.7)) }
                c.setFillColor(eye)
            }
            let m = mouthAt, k = r * 0.55
            c.setLineWidth(r * 0.4)
            switch mood {
            case .speak:
                let h = max(r * 0.15, r * 1.4 * mouth), w = r * (0.9 + 0.3 * mouth)
                c.fillEllipse(in: CGRect(x: m.x - w, y: m.y - h, width: w * 2, height: h * 2))
            case .happy:
                c.move(to: CGPoint(x: m.x - r * 1.1, y: m.y + r * 0.3))
                c.addArc(center: CGPoint(x: m.x, y: m.y + r * 0.3), radius: r * 1.1, startAngle: .pi, endAngle: 0, clockwise: false)
                c.closePath()
                c.fillPath()
            case .listen:
                // A soft, wide smile.
                c.addArc(center: CGPoint(x: m.x, y: m.y + k * 0.9), radius: k * 1.25, startAngle: .pi * 1.25, endAngle: .pi * 1.75, clockwise: false)
                c.strokePath()
            case .think:
                // "Hmm": a short line pushed to the side, slightly tilted.
                c.move(to: CGPoint(x: m.x - k * 0.2, y: m.y - k * 0.1))
                c.addLine(to: CGPoint(x: m.x - k * 1.4, y: m.y + k * 0.3))
                c.strokePath()
            case .sleep:
                c.strokeEllipse(in: CGRect(x: m.x - k * 0.45, y: m.y - k * 0.45, width: k * 0.9, height: k * 0.9))
            case .error:
                c.move(to: CGPoint(x: m.x - k * 1.4, y: m.y))
                for i in 1...4 { c.addLine(to: CGPoint(x: m.x - k * 1.4 + CGFloat(i) * k * 0.7, y: m.y + (i % 2 == 1 ? k * 0.5 : 0))) }
                c.strokePath()
            case .idle:
                c.move(to: CGPoint(x: m.x - k, y: m.y + k * 0.5))
                c.addLine(to: CGPoint(x: m.x, y: m.y - k * 0.3))
                c.addLine(to: CGPoint(x: m.x + k, y: m.y + k * 0.5))
                c.strokePath()
            }
        }
    }
}

// MARK: - The robot

final class Robot {
    let root = SCNNode()
    private let spin = SCNNode(), tilt = SCNNode(), body = SCNNode()
    private let head = SCNNode(), armNeg = SCNNode(), armPos = SCNNode(), legNeg = SCNNode(), legPos = SCNNode()
    private let earNeg = SCNNode(), earPos = SCNNode()
    private let screen: SCNMaterial
    private var thrusters: [Thruster] = [], feet: [Thruster] = []
    private let gear = SCNNode(), bubbles = SCNNode(), zs = SCNNode()
    private let sparkles = SCNParticleSystem(), smoke = SCNParticleSystem(), sparks = SCNParticleSystem()
    private let effects = SCNNode()
    var face = Face()
    private var drawnFace = ""
    var poke: CGFloat = -10   // time of the last click: a happy reaction
    var lean: CGFloat = 0     // sideways lean while the window moves
    var grounded = false      // standing on the bottom edge of the screen, engines off
    var walk: CGFloat = 0     // -1 walking left, 1 right, 0 standing
    let mouthMark = SCNNode() // where the speech bubble points
    private var solids: [SCNNode] = [], armSamples: [(SCNNode, [SCNVector3])] = []

    /// How many sample points of the hands and forearms are inside the head or the body (boxes shrunk by 12%).
    func penetration() -> Int {
        var hits = 0
        for solid in solids {
            let (a, b) = solid.boundingBox
            let c = SCNVector3((a.x + b.x) / 2, (a.y + b.y) / 2, (a.z + b.z) / 2)
            let h = SCNVector3((b.x - a.x) * 0.44, (b.y - a.y) * 0.44, (b.z - a.z) * 0.44)
            for (n, points) in armSamples {
                for p in points {
                    let q = solid.convertPosition(n.convertPosition(p, to: nil), from: nil)
                    if abs(q.x - c.x) < h.x && abs(q.y - c.y) < h.y && abs(q.z - c.z) < h.z { hits += 1 }
                }
            }
        }
        return hits
    }

    init(model: URL) throws {
        let (modelRoot, named) = try RobotGLTF.load(model)
        root.addChildNode(spin)
        spin.addChildNode(tilt)
        tilt.position = SCNVector3(0, 0.85, 0)
        tilt.addChildNode(body)
        body.position = SCNVector3(0, -0.85, 0)
        body.addChildNode(modelRoot)
        for (node, parent, at) in [(head, body, SCNVector3(0, 1.1, 0)), (armNeg, body, SCNVector3(-0.23, 0.97, 0)), (armPos, body, SCNVector3(0.23, 0.97, 0)),
                                   (legNeg, body, SCNVector3(-0.1, 0.56, 0)), (legPos, body, SCNVector3(0.1, 0.56, 0)),
                                   (earNeg, head, SCNVector3(-0.3, 1.39, -0.02)), (earPos, head, SCNVector3(0.3, 1.39, -0.02))] {
            parent.addChildNode(node)
            node.setWorldTransform(SCNMatrix4MakeTranslation(at.x, at.y, at.z))
        }
        var holders: [String: SCNNode] = [:]
        for (name, n) in named { if let h = n.childNodes.first(where: { $0.geometry != nil }) { holders[name] = h } }
        func holder(_ name: String) -> SCNNode? { holders[name] }
        // The rig needs this model's parts; another model is refused (JARVIS then shows the satellite).
        for part in ["head", "screen", "body", "bag", "arms_high", "legs_high", "ears"] where holders[part] == nil {
            throw RobotGLTF.Failure(message: "manca la parte \(part) nel modello")
        }
        func move(_ n: SCNNode, _ to: SCNNode) { let w = n.worldTransform; to.addChildNode(n); n.setWorldTransform(w) }
        for name in ["head", "screen", "cap", "ears_part_1", "ears_part_2"] { if let h = holder(name) { move(h, head) } }
        func split(_ names: [String], _ neg: SCNNode, _ pos: SCNNode) {
            for name in names {
                guard let h = holder(name) else { continue }
                let w = h.worldTransform
                let (a, b) = splitByX(h)
                h.removeFromParentNode()
                a.name = name; b.name = name
                neg.addChildNode(a); a.setWorldTransform(w)
                pos.addChildNode(b); b.setWorldTransform(w)
            }
        }
        split(["arms_high", "arms_high.002", "arms_high.003", "arm_joints", "arm_joints_2", "arm_joint_part", "Cube.001", "Cylinder.008", "Cylinder.010"], armNeg, armPos)
        split(["legs_high", "legs_high.001"], legNeg, legPos)
        split(["ears"], earNeg, earPos)
        solids = ["head", "cap", "screen", "body", "bag", "chest_high.003"].compactMap { holder($0) }
        // Hands and forearms: the parts that could reach the head or the body.
        for n in armNeg.childNodes + armPos.childNodes where ["Cylinder.008", "Cylinder.010", "Cube.001", "arms_high.002"].contains(n.name ?? "") {
            let g = n.geometry!, vs = g.sources(for: .vertex)[0], e = g.elements[0]
            let used: [Int] = e.data.withUnsafeBytes { raw in (0..<e.primitiveCount * 3).map { Int(raw.load(fromByteOffset: $0 * 4, as: UInt32.self)) } }
            let points: [SCNVector3] = vs.data.withUnsafeBytes { raw in
                Array(Set(used)).sorted().enumerated().filter { $0.offset % 25 == 0 }.map { _, v in
                    let o = vs.dataOffset + v * vs.dataStride
                    return SCNVector3(raw.load(fromByteOffset: o, as: Float.self), raw.load(fromByteOffset: o + 4, as: Float.self), raw.load(fromByteOffset: o + 8, as: Float.self))
                }
            }
            armSamples.append((n, points))
        }
        // Only the model itself reacts to the mouse (category 2), not flames or effects.
        body.enumerateHierarchy { n, _ in if n.geometry != nil { n.categoryBitMask = 3 } }
        head.addChildNode(mouthMark)
        mouthMark.setWorldTransform(SCNMatrix4MakeTranslation(0, 1.31, 0.27))

        // The screen gets its own material, so its face can be redrawn.
        guard let screenNode = holder("screen") else { throw RobotGLTF.Failure(message: "manca lo schermo") }
        screenNode.geometry = screenNode.geometry!.copy() as? SCNGeometry
        screen = screenNode.geometry!.firstMaterial!.copy() as! SCNMaterial
        screenNode.geometry!.materials = [screen]
        screen.emission.contentsTransform = FaceMap.transform
        screen.emission.wrapS = .clampToBorder
        screen.emission.wrapT = .clampToBorder
        screen.emission.borderColor = NSColor.black
        screen.emission.intensity = 1.3
        // The original face is also printed in grey in the base colour: paint it over with the screen's own dark.
        if let base = screen.diffuse.contents as! CGImage? {
            let w = base.width, h = base.height
            let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0,
                                space: CGColorSpace(name: CGColorSpace.sRGB)!, bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
            ctx.draw(base, in: CGRect(x: 0, y: 0, width: w, height: h))
            // Sample the screen beside the face (u 0.375, v 0.47), then cover u 0.392–0.458, v 0.40–0.55.
            let px = ctx.data!.assumingMemoryBound(to: UInt8.self)
            let sx = Int(0.375 * Double(w)), sy = Int(0.47 * Double(h))   // rows run from the top
            let o = sy * ctx.bytesPerRow + sx * 4
            ctx.setFillColor(CGColor(srgbRed: CGFloat(px[o]) / 255, green: CGFloat(px[o + 1]) / 255, blue: CGFloat(px[o + 2]) / 255, alpha: 1))
            let rect = CGRect(x: 0.392 * Double(w), y: Double(h) * (1 - 0.55), width: 0.066 * Double(w), height: 0.15 * Double(h))
            ctx.fill(rect)
            screen.diffuse.contents = ctx.makeImage()
        }

        // Jet nozzles under the backpack, and small ones under the feet.
        let metal = SCNMaterial()
        metal.lightingModel = .physicallyBased
        metal.diffuse.contents = NSColor(white: 0.2, alpha: 1)
        metal.metalness.contents = 0.85
        metal.roughness.contents = 0.3
        for x in [-0.09, 0.09] as [CGFloat] {
            let bell = SCNNode(geometry: SCNCone(topRadius: 0.028, bottomRadius: 0.045, height: 0.06))
            bell.geometry?.materials = [metal]
            bell.position = SCNVector3(x, 0.69, -0.22)
            body.addChildNode(bell)
            let t = Thruster(size: 1)
            t.node.position = SCNVector3(x, 0.655, -0.22)
            body.addChildNode(t.node)
            thrusters.append(t)
        }
        for (leg, x) in [(legNeg, -0.11), (legPos, 0.11)] as [(SCNNode, CGFloat)] {
            let t = Thruster(size: 0.6)
            leg.addChildNode(t.node)
            t.node.setWorldTransform(SCNMatrix4MakeTranslation(x, -0.01, 0))
            feet.append(t)
        }

        // Effects for the states, around the head.
        tilt.addChildNode(effects)
        effects.position = SCNVector3(0, 0, 0)
        let holo = SCNMaterial()
        holo.lightingModel = .constant
        holo.diffuse.contents = cyan
        holo.transparency = 0.85
        holo.blendMode = .add
        holo.isDoubleSided = true
        let gearShape = SCNShape(path: { let p = gearPath(teeth: 8, outer: 0.13, inner: 0.1, hole: 0.045); p.flatness = 0.002; return p }(), extrusionDepth: 0.025)
        gearShape.materials = [holo]
        gear.geometry = gearShape
        gear.position = SCNVector3(0.32, 1.12, 0.05)
        effects.addChildNode(gear)
        for (i, r) in [0.025, 0.037, 0.05].enumerated() {
            let b = SCNNode(geometry: SCNSphere(radius: CGFloat(r)))
            b.geometry?.materials = [holo]
            b.position = SCNVector3(0.17 + CGFloat(i) * 0.06, 0.72 + CGFloat(i) * 0.12, 0.08)
            bubbles.addChildNode(b)
        }
        effects.addChildNode(bubbles)
        for _ in 0..<3 {
            let text = SCNText(string: "Z", extrusionDepth: 0.2)
            text.font = NSFont.systemFont(ofSize: 10, weight: .heavy)
            text.flatness = 0.1
            text.materials = [holo]
            let z = SCNNode(geometry: text)
            let (a, b) = z.boundingBox
            z.pivot = SCNMatrix4MakeTranslation((a.x + b.x) / 2, (a.y + b.y) / 2, 0)
            z.constraints = [SCNBillboardConstraint()]
            zs.addChildNode(z)
        }
        effects.addChildNode(zs)

        sparkles.particleImage = star
        sparkles.blendMode = .additive
        sparkles.birthRate = 0
        sparkles.particleLifeSpan = 1.0
        sparkles.emitterShape = SCNSphere(radius: 0.75)
        sparkles.birthLocation = .surface
        sparkles.particleVelocity = 0.15
        sparkles.particleSize = 0.07
        sparkles.particleSizeVariation = 0.04
        sparkles.particleAngularVelocity = 180
        sparkles.particleColor = NSColor(srgbRed: 1, green: 0.85, blue: 0.3, alpha: 1)
        sparkles.particleColorVariation = SCNVector4(0.35, 0.2, 0, 0)
        sparkles.propertyControllers = [.opacity: SCNParticlePropertyController(animation: { let a = CAKeyframeAnimation(); a.values = [0, 1, 0]; a.keyTimes = [0, 0.3, 1]; return a }())]
        sparkles.isLightingEnabled = false
        let sparkleNode = SCNNode()
        sparkleNode.position = SCNVector3(0, 0.1, 0)
        sparkleNode.addParticleSystem(sparkles)
        effects.addChildNode(sparkleNode)

        smoke.particleImage = softDot
        smoke.blendMode = .alpha
        smoke.birthRate = 0
        smoke.particleLifeSpan = 1.4
        smoke.particleColor = NSColor(white: 0.45, alpha: 0.5)
        smoke.particleVelocity = 0.25
        smoke.emittingDirection = SCNVector3(0, 1, 0)
        smoke.spreadingAngle = 25
        smoke.particleSize = 0.08
        smoke.propertyControllers = [.size: SCNParticlePropertyController(animation: { let a = CAKeyframeAnimation(); a.values = [0.7, 2.6]; return a }()),
                                     .opacity: SCNParticlePropertyController(animation: { let a = CAKeyframeAnimation(); a.values = [0.6, 0]; return a }())]
        smoke.isLightingEnabled = false
        sparks.particleImage = softDot
        sparks.blendMode = .additive
        sparks.birthRate = 0
        sparks.particleLifeSpan = 0.5
        sparks.particleColor = NSColor(srgbRed: 1, green: 0.8, blue: 0.3, alpha: 1)
        sparks.particleVelocity = 0.9
        sparks.particleVelocityVariation = 0.4
        sparks.spreadingAngle = 70
        sparks.emittingDirection = SCNVector3(0, 1, 0)
        sparks.particleSize = 0.02
        sparks.isAffectedByGravity = true
        sparks.acceleration = SCNVector3(0, -3, 0)
        sparks.isLightingEnabled = false
        let vent = SCNNode()
        vent.position = SCNVector3(0, 1.12, -0.22)
        body.addChildNode(vent)
        vent.addParticleSystem(smoke)
        vent.addParticleSystem(sparks)
    }

    /// Keeps an arm out of the head and the body, whatever the pose asks. side: 1 for the +x arm, -1 for the other.
    /// Measured on the model: raised sideways past 2.2 rad the hand reaches the head and the ear; brought inwards
    /// past the hanging line it goes into the chest, unless the arm is held well forward (then down to -0.35).
    static func armLimit(_ e: SCNVector3, side: CGFloat) -> SCNVector3 {
        var z = e.z * side                       // outward raise, in the +x arm's terms
        let forward = -e.x                       // positive when the arm is brought forward
        z = min(z, 2.2)
        z = max(z, forward > 0.4 ? -0.35 : 0.06)
        // Raised high, it may not also swing back or far forward (shoulder pads and the backpack).
        let x = z > 1.2 ? max(min(e.x, 0.25), -0.6) : max(min(e.x, 0.6), -1.3)
        return SCNVector3(x, e.y, z * side)
    }

    static let stepRate: CGFloat = 3.6, stepSwing: CGFloat = 0.2, legLength: CGFloat = 0.56
    /// How far the body travels per second while walking, in model units: two steps per swing cycle,
    /// each as long as the feet travel apart, so the planted foot does not slide.
    static var walkSpeed: CGFloat { 2 * legLength * sin(stepSwing) / (.pi / stepRate) }
    var walkAmount: CGFloat { smooth["walk"] ?? 0 }
    /// Points around the head at mouth height (its widest outline there), in world space, as it is drawn now.
    func headOutline() -> [SCNVector3] {
        (0..<24).map { i in
            let a = CGFloat(i) / 24 * 2 * .pi
            return head.presentation.convertPosition(SCNVector3(cos(a) * 0.29, 0.21, sin(a) * 0.27), to: nil)
        }
    }
    var facing: CGFloat { spin.eulerAngles.y }

    func flames(_ s: FlameStyle) { (thrusters + feet).forEach { $0.style(s) } }

    private var smooth: [String: CGFloat] = [:]
    private func ease(_ key: String, _ target: CGFloat, _ rate: CGFloat = 0.1) -> CGFloat {
        let v = (smooth[key] ?? target) + (target - (smooth[key] ?? target)) * rate
        smooth[key] = v
        return v
    }

    func update(t: CGFloat, mood: Mood, level: CGFloat) {
        let idle = mood == .idle, listen = mood == .listen, think = mood == .think, speak = mood == .speak
        let sleep = mood == .sleep, error = mood == .error
        let poked = t - poke < 2.4
        let happy = mood == .happy || poked
        // Little routines while waiting: a wave every 14 s, a look around, a pirouette every 26 s.
        let waveT = t.truncatingRemainder(dividingBy: 14)
        let g = ease("ground", grounded ? 1 : 0, 0.1)            // 1 on the ground
        let w = ease("walk", walk != 0 && grounded ? 1 : 0, 0.12) // 1 while walking
        // Walking: rigid legs swing about the hips; the body rides over the planted foot (an inverted pendulum),
        // so it dips a little at each step, sways onto the stance leg and leans into the walk.
        let phase = t * Self.stepRate
        let legSwing = Self.stepSwing * sin(phase) * w
        let waving = (idle && waveT > 10 && waveT < 12.4 && w < 0.1) || poked
        let spinT = t.truncatingRemainder(dividingBy: 26)
        let pirouette = idle && spinT > 20 && spinT < 21.4 && !grounded
        // Thinking: the gear drifts from side to side above the head, and head and eyes follow it.
        let gx = sin(t * 0.55)
        let glance = ease("glance", w > 0.5 && t.truncatingRemainder(dividingBy: 7) > 5.2 ? 1 : 0, 0.08)
        let happySpin = mood == .happy && t.truncatingRemainder(dividingBy: 4) < 1.1

        // Floating: a slow bob; lower and calmer asleep; jittery on error.
        let bob = (sin(t * 1.7) * (sleep ? 0.02 : 0.045)) * (1 - g) + (happy ? abs(sin(t * 6)) * 0.06 : 0) + Self.legLength * (cos(legSwing) - 1)
        let shake = error ? sin(t * 37) * 0.012 : 0
        root.position = SCNVector3(shake, ease("base", sleep && !grounded ? -0.12 : 0, 0.04) + bob, 0)
        // Spins.
        var yaw = ease("yaw", walk > 0 && grounded ? 1.15 : walk < 0 && grounded ? -1.15 : 0, 0.08)
        if pirouette { yaw += (spinT - 20) / 1.4 * 2 * .pi }
        if happySpin { yaw += t.truncatingRemainder(dividingBy: 4) / 1.1 * 2 * .pi }
        spin.eulerAngles.y = yaw
        tilt.eulerAngles = SCNVector3(ease("lean", listen ? 0.14 : sleep ? 0.08 : speak ? 0.05 : 0) + sin(t * 0.9) * 0.025 * (1 - w) + 0.06 * w,
                                      0, ease("side", -lean * 0.6, 0.08) + sin(t * 0.7) * 0.03 * (1 - g) + (think ? sin(t * 0.55 + 0.6) * 0.05 : 0)
                                          + sin(phase) * 0.035 * w)
        // Head.
        let lookT = t.truncatingRemainder(dividingBy: 9)
        let lookAround: CGFloat = idle ? (lookT > 5 && lookT < 6.8 ? 0.45 : lookT > 6.8 && lookT < 8 ? -0.35 : 0) : 0
        head.eulerAngles = SCNVector3(ease("hp", sleep ? 0.3 : think ? -0.15 : speak ? sin(t * 4.5) * 0.05 + level * 0.06 : listen ? 0.05 + max(0, sin(t * 3.2)) * 0.07 * (sin(t * 0.7) > 0.3 ? 1 : 0) : sin(t * 0.6) * 0.04),
                                      ease("hy", lookAround + (think ? gx * 0.22 : 0) - walk * 0.75 * glance, 0.08),
                                      ease("hr", listen ? -0.16 : think ? -gx * 0.1 : happy ? sin(t * 6) * 0.14 : sleep ? -0.1 : 0))
        // Arms: hang and sway; wave with the +x arm; flail on error; up for joy.
        let sway = sin(t * 1.7 + 0.5) * 0.06
        let wave = waving ? ease("wave", 1.85, 0.15) + sin(t * 11) * 0.28 : ease("wave", 0, 0.12)
        let cheer: CGFloat = mood == .happy ? ease("cheer", 0.9 + sin(t * 6) * 0.2) : ease("cheer", 0)
        let flail: CGFloat = error ? sin(t * 9) * 0.5 : 0
        let attend = ease("attend", listen ? 1 : 0, 0.1)   // listening: hands together in front, attentive
        // Thinking: one hand raised in front, fingers tapping; the other hangs and sways.
        let ponder = ease("ponder", think ? 1 : 0, 0.08)
        let tap = sin(t * 8) * 0.07 * ponder
        let swing = sin(phase) * 0.26 * w   // arms swing against the legs
        armPos.eulerAngles = Self.armLimit(SCNVector3(sway * (1 - attend) * (1 - w) - attend * 0.55 + swing, 0, 0.12 + wave + cheer + flail - attend * 0.3), side: 1)
        armNeg.eulerAngles = Self.armLimit(SCNVector3(-sway * (1 - attend) * (1 - w) - attend * 0.55 - ponder * 0.85 + tap - swing, 0,
                                                      -0.12 - cheer + flail * 0.7 + attend * 0.3 + ponder * 0.12), side: -1)
        // Legs dangle, out of step.
        let dangle = sleep ? 0.05 : 0.11
        legNeg.eulerAngles = SCNVector3((0.12 + sin(t * 1.7) * dangle) * (1 - g) + legSwing, 0, -0.04)
        legPos.eulerAngles = SCNVector3((0.12 + sin(t * 1.7 + 2.2) * dangle) * (1 - g) - legSwing, 0, 0.04)
        // Ears: perk forward to listen, droop asleep, wiggle for joy.
        let ear = ease("ear", listen ? 0.4 : sleep ? -0.3 : 0)
        earNeg.eulerAngles.x = ear + (happy ? sin(t * 12) * 0.25 : sin(t * 2.1) * 0.04)
        earPos.eulerAngles.x = ear + (happy ? sin(t * 12 + 1.5) * 0.25 : sin(t * 2.3 + 1) * 0.04)

        // Thrusters: steady, pulsing with the voice, low asleep, sputtering on error.
        let sputter: CGFloat = error ? (sin(t * 23) + sin(t * 37) > 0.4 ? 1 : 0.08) : 1
        let power = (ease("power", sleep ? 0.35 : happy ? 1.3 : speak ? 0.9 + level * 0.5 : 1, 0.15) * sputter + cos(t * 1.7) * 0.12) * (1 - g)
        for (i, th) in thrusters.enumerated() { th.update(power: power, flicker: CGFloat.random(in: 0...1) * 0.6 + CGFloat(i) * 0.1) }
        for th in feet { th.update(power: power * 0.85, flicker: CGFloat.random(in: 0...1)) }

        // Face.
        let cycle = t.truncatingRemainder(dividingBy: 3.7)
        let blink: CGFloat = cycle < 0.14 ? abs(cycle - 0.07) / 0.07 : (t.truncatingRemainder(dividingBy: 11.1) < 0.4 && cycle > 0.3 && cycle < 0.44 ? 0.1 : 1)
        face.mood = poked ? .happy : mood
        face.happyEyes = waving
        face.t = t
        face.blink = blink
        face.eyeScale = ease("es", listen ? 1.1 : speak ? 1 + level * 0.1 : 1)
        face.look = CGPoint(x: ease("lx", think ? -gx * 0.011 : idle ? lookAround * -0.04 : 0), y: ease("ly", think ? 0.007 : sleep ? -0.004 : 0))
        face.mouth = ease("mouth", speak ? level : 0, 0.4)
        if face.key != drawnFace {
            drawnFace = face.key
            screen.emission.contents = face.draw()
        }
        screen.emission.intensity = sleep ? 0.6 : 1.3

        // Effects.
        gear.opacity = ease("gear", think ? 1 : 0, 0.08)
        gear.eulerAngles = SCNVector3(0, sin(t * 0.8) * 0.4, -t * 2)
        gear.position = SCNVector3(gx * 0.3, 1.12, 0.05)
        for (i, b) in bubbles.childNodes.enumerated() {
            let on = think && (t * 1.6).truncatingRemainder(dividingBy: 3.2) > CGFloat(i) * 0.6
            b.opacity = ease("bub\(i)", on ? 0.9 : 0, 0.2)
            // A trail from the top of the head to the gear.
            let f = 0.3 + CGFloat(i) * 0.2
            b.position = SCNVector3(gx * 0.3 * f + 0.05, 0.86 + (1.02 - 0.86) * f, 0.08)
        }
        for (i, z) in zs.childNodes.enumerated() {
            let p = (t * 0.35 + CGFloat(i) / 3).truncatingRemainder(dividingBy: 1)
            z.position = SCNVector3(0.18 + p * 0.25 + sin(p * 6) * 0.04, 0.75 + p * 0.55, 0.05)
            let s = 0.012 + p * 0.01
            z.scale = SCNVector3(s, s, s)
            z.opacity = sleep ? sin(p * .pi) * ease("zs", 1, 0.05) : ease("zs", 0, 0.2) * sin(p * .pi)
        }
        sparkles.birthRate = mood == .happy || poked ? 30 : 0
        smoke.birthRate = error ? 14 : 0
        sparks.birthRate = error && sin(t * 5) > 0.6 ? 60 : 0
    }
}

// MARK: - Scene

static func makeScene(model: URL) throws -> (SCNScene, Robot, SCNNode) {
    let scene = SCNScene()
    scene.lightingEnvironment.contents = studio()
    scene.lightingEnvironment.intensity = 1.3
    let robot = try Robot(model: model)
    scene.rootNode.addChildNode(robot.root)
    let camera = SCNNode()
    camera.camera = SCNCamera()
    camera.camera?.fieldOfView = 29
    camera.position = SCNVector3(1.0, 1.25, 5.4)
    camera.look(at: SCNVector3(0, 0.8, 0))
    scene.rootNode.addChildNode(camera)
    let key = SCNLight(); key.type = .directional; key.intensity = 1100; key.color = NSColor(srgbRed: 1, green: 0.96, blue: 0.9, alpha: 1)
    let keyNode = SCNNode(); keyNode.light = key; keyNode.eulerAngles = SCNVector3(-0.6, 0.5, 0)
    let rim = SCNLight(); rim.type = .directional; rim.intensity = 900; rim.color = cyan
    let rimNode = SCNNode(); rimNode.light = rim; rimNode.eulerAngles = SCNVector3(-0.3, 2.7, 0)
    let fill = SCNLight(); fill.type = .ambient; fill.intensity = 180
    let fillNode = SCNNode(); fillNode.light = fill
    [keyNode, rimNode, fillNode].forEach(scene.rootNode.addChildNode)
    return (scene, robot, camera)
}

static func voiceLevel(_ t: CGFloat) -> CGFloat { max(0, 0.5 + 0.5 * sin(t * 13)) * (0.55 + 0.45 * sin(t * 2.1)) }


// MARK: - Speech bubble

/// A comic speech bubble beside the head, its tail pointing at the mouth; the text types itself in.
final class BubbleView: NSView {
    static let tailW: CGFloat = 26, rise: CGFloat = 20, pad: CGFloat = 11, margin: CGFloat = 5, maxWidth: CGFloat = 230, maxHeight: CGFloat = 170
    var tailLeft = true
    let scroll = NSScrollView()
    let text = NSTextView()

    override init(frame: NSRect) {
        super.init(frame: frame)
        text.isEditable = false
        text.isSelectable = false
        text.drawsBackground = false
        text.font = .systemFont(ofSize: 13, weight: .medium)
        text.textColor = NSColor(srgbRed: 0.11, green: 0.13, blue: 0.19, alpha: 1)
        text.textContainerInset = .zero
        text.textContainer?.lineFragmentPadding = 0
        scroll.documentView = text
        scroll.drawsBackground = false
        scroll.contentView.drawsBackground = false
        scroll.hasVerticalScroller = false
        addSubview(scroll)
    }
    required init?(coder: NSCoder) { fatalError() }

    /// Size of the text area for a string.
    func measure(_ string: String) -> NSSize {
        let storage = NSTextStorage(string: string, attributes: [.font: text.font!])
        let container = NSTextContainer(size: NSSize(width: Self.maxWidth, height: .greatestFiniteMagnitude))
        container.lineFragmentPadding = 0
        let layout = NSLayoutManager()
        layout.addTextContainer(container)
        storage.addLayoutManager(layout)
        layout.glyphRange(for: container)
        let used = layout.usedRect(for: container)
        return NSSize(width: max(24, ceil(used.width) + 6), height: min(Self.maxHeight, max(17, ceil(used.height))))
    }

    var bubbleRect: NSRect {
        NSRect(x: tailLeft ? Self.tailW : Self.margin, y: Self.margin + Self.rise, width: bounds.width - Self.tailW - Self.margin, height: bounds.height - Self.margin * 2 - Self.rise)
    }
    /// Height of the tail's tip in the panel: level with the mouth, below the bubble, which sits higher (beside the head's top).
    static var tipY: CGFloat { margin + 2 }
    static func size(for content: NSSize) -> NSSize {
        NSSize(width: content.width + pad * 2 + tailW + margin, height: content.height + (pad - 2) * 2 + margin * 2 + rise)
    }

    func layoutText() {
        let r = bubbleRect.insetBy(dx: Self.pad, dy: Self.pad - 2)
        scroll.frame = r
        text.frame = NSRect(x: 0, y: 0, width: r.width, height: max(r.height, text.frame.height))
        text.textContainer?.containerSize = NSSize(width: r.width, height: .greatestFiniteMagnitude)
        text.sizeToFit()
        text.scrollToEndOfDocument(nil)
    }

    override func draw(_ dirtyRect: NSRect) {
        let r = bubbleRect.insetBy(dx: 1, dy: 1)
        let path = NSBezierPath(roundedRect: r, xRadius: 14, yRadius: 14)
        let tail = NSBezierPath()
        if tailLeft {
            // A wedge from the bubble's lower corner down to the mouth's height, just outside the head.
            tail.move(to: NSPoint(x: r.minX + 3, y: r.minY + 14))
            tail.line(to: NSPoint(x: 1, y: Self.tipY))
            tail.line(to: NSPoint(x: r.minX + 24, y: r.minY + 1))
        } else {
            tail.move(to: NSPoint(x: r.maxX - 24, y: r.minY + 1))   // same winding as the left one
            tail.line(to: NSPoint(x: bounds.width - 1, y: Self.tipY))
            tail.line(to: NSPoint(x: r.maxX - 3, y: r.minY + 14))
        }
        tail.close()
        path.append(tail)
        path.windingRule = .nonZero
        // One white shape, no outline: a soft shadow gives the edge on any background.
        let shadow = NSShadow()
        shadow.shadowBlurRadius = 4
        shadow.shadowOffset = NSSize(width: 0, height: -1)
        shadow.shadowColor = NSColor(white: 0, alpha: 0.3)
        NSGraphicsContext.current?.saveGraphicsState()
        shadow.set()
        NSColor(white: 1, alpha: 0.97).setFill()
        path.fill()
        NSGraphicsContext.current?.restoreGraphicsState()
    }
}

final class Bubble {
    let panel: NSPanel
    let view = BubbleView(frame: .zero)
    private var full = "", typed = 0
    private var started: CGFloat = 0, finished: CGFloat = 0
    private(set) var typing = false
    private var side: Bool?   // true: bubble on the right of the mouth; chosen once per sentence

    init() {
        panel = NSPanel(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        panel.isOpaque = false
        panel.backgroundColor = .clear
        panel.hasShadow = false
        panel.level = .floating
        panel.ignoresMouseEvents = true
        panel.collectionBehavior = [.canJoinAllSpaces, .stationary, .ignoresCycle]
        panel.hidesOnDeactivate = false
        panel.isReleasedWhenClosed = false
        panel.contentView = view
    }

    private var shownID: UUID?

    func say(_ text: String, at t: CGFloat) { show(text, id: UUID(), at: t) }

    /// Shows an answer while it is still being written: the same id keeps what was already typed,
    /// a new id starts a new bubble.
    func show(_ text: String, id: UUID, at t: CGFloat) {
        let clean = text.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !clean.isEmpty else { return }
        if id != shownID || !panel.isVisible {
            shownID = id
            typed = 0
            side = nil
            started = t
            panel.alphaValue = 1
            panel.orderFrontRegardless()
        } else if clean == full {
            return
        } else {
            // Typing goes on from where it was.
            started = t - CGFloat(typed) / 38
        }
        full = clean
        typing = typed < full.count
    }

    func hide() {
        panel.orderOut(nil)
        shownID = nil
    }

    /// Advances the typing and keeps the bubble beside the mouth (a point on screen). Returns true while typing.
    /// left/right: the head's edges on screen at mouth height; the tail's tip stops just outside the one on the bubble's side.
    func update(t: CGFloat, mouth: NSPoint, left: CGFloat, right edge: CGFloat, screen: NSRect) -> Bool {
        guard panel.isVisible else { return false }
        if typing {
            let n = min(full.count, Int((t - started) * 38))
            if n != typed {
                typed = n
                view.text.string = String(full.prefix(n))
            }
            if n >= full.count { typing = false; finished = t }
        } else {
            // Stays long enough to read, then fades.
            let hold = max(2.5, CGFloat(full.count) / 16)
            let fade = (t - finished - hold) / 0.5
            if fade >= 1 { hide(); return false }
            panel.alphaValue = 1 - max(0, fade)
        }
        let content = view.measure(String(full.prefix(max(1, typed))))
        let size = BubbleView.size(for: content)
        if side == nil {
            let whole = view.measure(full)
            side = edge + 3 + BubbleView.size(for: whole).width < screen.maxX
        }
        let right = side!
        view.tailLeft = right
        // The tail points at the mouth from just outside the head; the bubble sits beside the head and grows upwards.
        var origin = NSPoint(x: right ? edge + 3 - 1 : left - 3 - size.width + 1, y: mouth.y - BubbleView.tipY)
        origin.y = min(origin.y, screen.maxY - size.height)
        let frame = NSRect(origin: origin, size: size)
        if frame != panel.frame {
            panel.setFrame(frame, display: false)
            view.frame = NSRect(origin: .zero, size: size)
            view.layoutText()
            view.needsDisplay = true
        } else if typing { view.layoutText() }
        return typing
    }
}

}
