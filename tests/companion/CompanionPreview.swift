// Standalone preview of the 3D companion (16 November): three small robot animals in JARVIS's style
// (white ceramic shell, dark glass face screen with LED eyes, cyan accents), animated through
// JARVIS's states. Not part of the app yet: the user chooses one here first.
//   swiftc -O CompanionPreview.swift -o preview && ./preview                (window)
//   ./preview --snapshot <dir>                                               (PNG of every animal and state)
import AppKit
import SceneKit

enum Mood: String, CaseIterable {
    case idle = "Attesa", listen = "Ascolta", think = "Pensa", speak = "Parla", happy = "Felice", sleep = "Dorme", error = "Errore"
}

enum Kind: String, CaseIterable { case fox = "Volpe", cat = "Gatto", owl = "Gufo" }

// MARK: - Materials

enum Look {
    static let cyan = NSColor(srgbRed: 0.31, green: 0.86, blue: 1.0, alpha: 1)
    static let amber = NSColor(srgbRed: 1.0, green: 0.70, blue: 0.28, alpha: 1)

    static func shell() -> SCNMaterial {
        let m = SCNMaterial()
        m.lightingModel = .physicallyBased
        m.diffuse.contents = NSColor(white: 0.94, alpha: 1)
        m.roughness.contents = 0.32
        m.metalness.contents = 0.0
        m.clearCoat.contents = 0.6
        m.clearCoatRoughness.contents = 0.12
        return m
    }
    static func graphite() -> SCNMaterial {
        let m = SCNMaterial()
        m.lightingModel = .physicallyBased
        m.diffuse.contents = NSColor(srgbRed: 0.20, green: 0.23, blue: 0.27, alpha: 1)
        m.roughness.contents = 0.38
        m.metalness.contents = 0.75
        return m
    }
    static func glass() -> SCNMaterial {
        let m = SCNMaterial()
        m.lightingModel = .physicallyBased
        m.diffuse.contents = NSColor(srgbRed: 0.02, green: 0.04, blue: 0.07, alpha: 1)
        m.roughness.contents = 0.06
        m.metalness.contents = 0.2
        m.clearCoat.contents = 1.0
        m.clearCoatRoughness.contents = 0.02
        return m
    }
    static func light(_ color: NSColor = cyan, _ intensity: CGFloat = 1) -> SCNMaterial {
        let m = SCNMaterial()
        m.lightingModel = .constant
        m.diffuse.contents = NSColor.black
        m.emission.contents = color
        m.emission.intensity = intensity
        return m
    }
    /// A soft studio for reflections: bright top, a window strip, dark floor.
    static func studio() -> NSImage {
        let size = NSSize(width: 512, height: 256)
        let image = NSImage(size: size)
        image.lockFocus()
        NSGradient(colors: [NSColor(white: 0.95, alpha: 1), NSColor(srgbRed: 0.45, green: 0.55, blue: 0.68, alpha: 1),
                            NSColor(srgbRed: 0.05, green: 0.08, blue: 0.13, alpha: 1)])!.draw(in: NSRect(origin: .zero, size: size), angle: -90)
        NSColor(white: 1, alpha: 0.9).setFill()
        NSRect(x: 140, y: 170, width: 90, height: 40).fill()
        NSColor(srgbRed: 0.4, green: 0.85, blue: 1, alpha: 0.6).setFill()
        NSRect(x: 360, y: 120, width: 60, height: 30).fill()
        image.unlockFocus()
        return image
    }
}

func node(_ g: SCNGeometry, _ m: SCNMaterial, _ p: SCNVector3 = SCNVector3Zero) -> SCNNode {
    g.materials = [m]
    let n = SCNNode(geometry: g)
    n.position = p
    return n
}

func shape(_ path: NSBezierPath, depth: CGFloat = 0.012) -> SCNShape {
    path.flatness = 0.002
    return SCNShape(path: path, extrusionDepth: depth)
}

// MARK: - Eyes

/// The LED eyes on the face screen: their shape changes with the mood.
final class Eyes {
    let node = SCNNode()
    private let left = SCNNode(), right = SCNNode()
    private var shown: Mood?
    private let spacing: CGFloat, size: CGFloat
    private let mat = Look.light(Look.cyan, 1.4)

    init(spacing: CGFloat, size: CGFloat) {
        self.spacing = spacing
        self.size = size
        left.position = SCNVector3(-spacing, 0, 0)
        right.position = SCNVector3(spacing, 0, 0)
        node.addChildNode(left)
        node.addChildNode(right)
    }

    private func geometry(for mood: Mood, mirrored: Bool) -> SCNGeometry {
        let w = size * 0.72, h = size
        let path: NSBezierPath
        switch mood {
        case .happy:
            // An upturned arc: ^ ^
            path = NSBezierPath()
            path.appendArc(withCenter: .zero, radius: w * 0.62, startAngle: 20, endAngle: 160)
            path.appendArc(withCenter: .zero, radius: w * 0.30, startAngle: 160, endAngle: 20, clockwise: true)
            path.close()
        case .sleep:
            path = NSBezierPath(roundedRect: NSRect(x: -w * 0.6, y: -h * 0.06, width: w * 1.2, height: h * 0.12), xRadius: h * 0.06, yRadius: h * 0.06)
        case .error:
            // Narrowed, slanted: annoyed.
            path = NSBezierPath(roundedRect: NSRect(x: -w / 2, y: -h * 0.22, width: w, height: h * 0.44), xRadius: w * 0.3, yRadius: w * 0.3)
            var t = AffineTransform(rotationByDegrees: mirrored ? -18 : 18)
            path.transform(using: t)
            t = AffineTransform.identity
        default:
            path = NSBezierPath(roundedRect: NSRect(x: -w / 2, y: -h / 2, width: w, height: h), xRadius: w * 0.45, yRadius: w * 0.45)
        }
        let g = shape(path)
        g.materials = [mat]
        return g
    }

    func show(_ mood: Mood) {
        guard mood != shown else { return }
        shown = mood
        left.geometry = geometry(for: mood, mirrored: false)
        right.geometry = geometry(for: mood, mirrored: true)
        mat.emission.contents = mood == .error ? Look.amber : Look.cyan
    }

    /// blink 0...1 (1 open), look in face units, scale for surprise and attention, glow for brightness.
    func update(blink: CGFloat, look: CGPoint, scale: CGFloat, glow: CGFloat) {
        for (n, side) in [(left, -1.0), (right, 1.0)] {
            n.position = SCNVector3(CGFloat(side) * spacing + look.x, look.y, 0)
            n.eulerAngles.y = CGFloat(side) * 0.32
            n.scale = SCNVector3(scale, scale * max(0.06, blink), 1)
        }
        mat.emission.intensity = glow
    }
}

// MARK: - The robot animal

final class Companion {
    let root = SCNNode()
    let kind: Kind
    private let head = SCNNode(), body = SCNNode(), neck = SCNNode()
    private var earL = SCNNode(), earR = SCNNode()
    private var earLights: [SCNMaterial] = []
    private var tail: [SCNNode] = []
    private var wingL = SCNNode(), wingR = SCNNode()
    private let eyes: Eyes
    private var mouth: [SCNNode] = []
    private let chest = Look.light(Look.cyan, 1.2)
    private let thoughts = SCNNode()
    private var beak = SCNNode()

    init(_ kind: Kind) {
        self.kind = kind
        let shell = Look.shell(), glass = Look.glass(), metal = Look.graphite()
        eyes = Eyes(spacing: kind == .owl ? 0.2 : 0.165, size: kind == .owl ? 0.17 : 0.2)
        root.addChildNode(body)
        body.addChildNode(neck)
        neck.addChildNode(head)

        switch kind {
        case .fox, .cat:
            // Body: a sitting capsule, slightly flattened front to back.
            let torso = node(SCNCapsule(capRadius: 0.3, height: 0.8), shell, SCNVector3(0, -0.72, -0.02))
            torso.scale = SCNVector3(1, 1, 0.86)
            body.addChildNode(torso)
            body.addChildNode(node(SCNCylinder(radius: 0.31, height: 0.03), metal, SCNVector3(0, -0.5, -0.02)))
            let light = node(SCNCylinder(radius: 0.055, height: 0.01), chest, SCNVector3(0, -0.66, 0.24))
            light.eulerAngles.x = .pi / 2
            body.addChildNode(light)
            for x in [-0.15, 0.15] {
                body.addChildNode(node(SCNCapsule(capRadius: 0.075, height: 0.26), shell, SCNVector3(x, -1.02, 0.17)))
                body.addChildNode(node(SCNSphere(radius: 0.085), metal, SCNVector3(x, -1.1, 0.24)))
            }
            neck.position = SCNVector3(0, -0.36, 0)
            neck.addChildNode(node(SCNCylinder(radius: 0.12, height: 0.16), metal, SCNVector3(0, 0, 0)))
            head.position = SCNVector3(0, 0.36, 0)
            let skull = node(SCNSphere(radius: 0.5), shell)
            skull.scale = kind == .fox ? SCNVector3(1.12, 0.9, 1.0) : SCNVector3(1.08, 0.94, 0.98)
            head.addChildNode(skull)
            let fox = kind == .fox
            let visor = node(SCNSphere(radius: 0.5), glass, SCNVector3(0, fox ? 0.03 : -0.02, 0.2))
            visor.scale = SCNVector3(0.86, fox ? 0.54 : 0.6, 0.68)
            head.addChildNode(visor)
            eyes.node.position = SCNVector3(0, fox ? 0.06 : 0.0, 0.535)
            head.addChildNode(eyes.node)
            // Ears: tall and wide for the fox, small and sharp for the cat, with a light inside.
            let (r, h, x, tilt): (CGFloat, CGFloat, CGFloat, CGFloat) = kind == .fox ? (0.17, 0.42, 0.3, 0.42) : (0.13, 0.27, 0.29, 0.32)
            for side in [-1.0, 1.0] as [CGFloat] {
                let ear = SCNNode()
                ear.position = SCNVector3(side * x, 0.3, -0.04)
                ear.eulerAngles.z = -side * tilt
                let outer = node(SCNCone(topRadius: 0.01, bottomRadius: r, height: h), shell, SCNVector3(0, h / 2, 0))
                outer.scale = SCNVector3(1, 1, 0.55)
                let glow = Look.light(Look.cyan, 0.5)
                earLights.append(glow)
                let inner = node(SCNCone(topRadius: 0.005, bottomRadius: r * 0.55, height: h * 0.72), glow, SCNVector3(0, h * 0.4, r * 0.3))
                inner.scale = SCNVector3(1, 1, 0.3)
                ear.addChildNode(outer)
                ear.addChildNode(inner)
                if kind == .fox {
                    let tip = node(SCNCone(topRadius: 0.004, bottomRadius: r * 0.36, height: h * 0.3), metal, SCNVector3(0, h * 0.86, 0))
                    tip.scale = SCNVector3(1.05, 1, 0.6)
                    ear.addChildNode(tip)
                }
                head.addChildNode(ear)
                if side < 0 { earL = ear } else { earR = ear }
            }
            if kind == .cat {
                // Light whiskers on the cheeks.
                for side in [-1.0, 1.0] as [CGFloat] {
                    for i in 0..<3 {
                        let w = node(SCNCapsule(capRadius: 0.008, height: 0.16), Look.light(Look.cyan, 0.9),
                                     SCNVector3(side * 0.47, -0.1 - CGFloat(i) * 0.05, 0.22))
                        w.eulerAngles = SCNVector3(0, 0, .pi / 2 + side * CGFloat(i - 1) * 0.18)
                        head.addChildNode(w)
                    }
                }
            } else {
                // The fox's muzzle: a short snout under the screen, with a graphite nose.
                let muzzle = node(SCNSphere(radius: 0.2), shell, SCNVector3(0, -0.27, 0.34))
                muzzle.scale = SCNVector3(1.05, 0.62, 1.2)
                head.addChildNode(muzzle)
                head.addChildNode(node(SCNSphere(radius: 0.045), metal, SCNVector3(0, -0.23, 0.59)))
                // Cheek fluff, pointing out and down.
                for side in [-1.0, 1.0] as [CGFloat] {
                    let fluff = node(SCNCone(topRadius: 0.005, bottomRadius: 0.12, height: 0.26), shell, SCNVector3(side * 0.52, -0.2, 0.05))
                    fluff.eulerAngles.z = side * 2.0
                    fluff.scale = SCNVector3(1, 1, 0.5)
                    head.addChildNode(fluff)
                }
            }
            // Tail: a chain of segments behind, big and fluffy for the fox, thin and curled for the cat.
            let count = kind == .fox ? 9 : 13
            var parent = body
            for i in 0..<count {
                let f = CGFloat(i) / CGFloat(count - 1)
                let radius: CGFloat = kind == .fox ? 0.09 + 0.12 * sin(.pi * min(1, f * 1.15)) : 0.055 - 0.015 * f
                let segment = SCNNode()
                segment.position = i == 0 ? SCNVector3(0.2, -1.02, -0.12) : SCNVector3(0, kind == .fox ? 0.13 : 0.085, 0)
                let ball = node(SCNSphere(radius: radius), i == count - 1 ? Look.light(Look.cyan, 1.1) : (i % 3 == 2 ? metal : shell))
                if kind == .fox && i == count - 1 { ball.geometry = SCNSphere(radius: radius * 1.1); ball.geometry?.materials = [Look.light(Look.cyan, 1.1)] }
                segment.addChildNode(ball)
                parent.addChildNode(segment)
                tail.append(segment)
                parent = segment
            }
        case .owl:
            neck.position = SCNVector3(0, -0.2, 0)
            let egg = node(SCNSphere(radius: 0.58), shell, SCNVector3(0, -0.45, 0))
            egg.scale = SCNVector3(1, 1.12, 0.94)
            body.addChildNode(egg)
            let belly = node(SCNSphere(radius: 0.4), metal, SCNVector3(0, -0.62, 0.22))
            belly.scale = SCNVector3(0.9, 1.0, 0.55)
            body.addChildNode(belly)
            let light = node(SCNCylinder(radius: 0.05, height: 0.01), chest, SCNVector3(0, -0.6, 0.455))
            light.eulerAngles.x = .pi / 2
            body.addChildNode(light)
            for x in [-0.17, 0.17] { body.addChildNode(node(SCNCapsule(capRadius: 0.06, height: 0.2), metal, SCNVector3(x, -1.12, 0.12))) }
            for side in [-1.0, 1.0] as [CGFloat] {
                let wing = SCNNode()
                wing.position = SCNVector3(side * 0.55, -0.35, -0.02)
                let blade = node(SCNSphere(radius: 0.3), shell, SCNVector3(side * 0.04, -0.2, 0))
                blade.scale = SCNVector3(0.32, 1.0, 0.75)
                wing.addChildNode(blade)
                wing.addChildNode(node(SCNCapsule(capRadius: 0.012, height: 0.3), Look.light(Look.cyan, 0.8), SCNVector3(side * 0.12, -0.22, 0.1)))
                body.addChildNode(wing)
                if side < 0 { wingL = wing } else { wingR = wing }
            }
            head.position = SCNVector3(0, 0.25, 0)
            let skull = node(SCNSphere(radius: 0.5), shell)
            skull.scale = SCNVector3(1.12, 0.86, 0.95)
            head.addChildNode(skull)
            let face = node(SCNSphere(radius: 0.5), glass, SCNVector3(0, -0.02, 0.2))
            face.scale = SCNVector3(0.95, 0.66, 0.66)
            head.addChildNode(face)
            // Two lens rings around the eyes.
            for side in [-1.0, 1.0] as [CGFloat] {
                let ring = node(SCNTorus(ringRadius: 0.145, pipeRadius: 0.018), metal, SCNVector3(side * 0.2, 0.0, 0.5))
                ring.eulerAngles.x = .pi / 2
                ring.eulerAngles.z = -side * 0.4
                head.addChildNode(ring)
            }
            eyes.node.position = SCNVector3(0, 0, 0.53)
            head.addChildNode(eyes.node)
            beak = node(SCNCone(topRadius: 0, bottomRadius: 0.05, height: 0.1), Look.light(Look.amber, 0.9), SCNVector3(0, -0.2, 0.5))
            beak.eulerAngles.x = .pi
            head.addChildNode(beak)
            for side in [-1.0, 1.0] as [CGFloat] {
                let tuft = SCNNode()
                tuft.position = SCNVector3(side * 0.36, 0.3, -0.02)
                tuft.eulerAngles.z = -side * 0.6
                let cone = node(SCNCone(topRadius: 0.005, bottomRadius: 0.1, height: 0.24), shell, SCNVector3(0, 0.12, 0))
                cone.scale = SCNVector3(1, 1, 0.5)
                tuft.addChildNode(cone)
                let glow = Look.light(Look.cyan, 0.5)
                earLights.append(glow)
                tuft.addChildNode(node(SCNCone(topRadius: 0.003, bottomRadius: 0.05, height: 0.16), glow, SCNVector3(0, 0.1, 0.035)))
                head.addChildNode(tuft)
                if side < 0 { earL = tuft } else { earR = tuft }
            }
        }
        // A small mouth of light bars under the eyes, shown while speaking.
        for i in 0..<(kind == .owl ? 0 : 5) {
            let bar = node(SCNBox(width: 0.026, height: 0.06, length: 0.01, chamferRadius: 0.012), Look.light(Look.cyan, 1.3),
                           SCNVector3(CGFloat(i - 2) * 0.045, kind == .fox ? -0.12 : -0.17, 0.5))
            head.addChildNode(bar)
            mouth.append(bar)
        }
        // "Thinking": three small lights circling above the head.
        for i in 0..<3 {
            let dot = node(SCNSphere(radius: 0.035), Look.light(Look.cyan, 1.4))
            dot.position = SCNVector3(cos(CGFloat(i) * 2.09) * 0.32, 0, sin(CGFloat(i) * 2.09) * 0.32)
            thoughts.addChildNode(dot)
        }
        thoughts.position = SCNVector3(0, 0.62, 0)
        head.addChildNode(thoughts)
        eyes.show(.idle)
    }

    private var smooth: [String: CGFloat] = [:]
    private func ease(_ key: String, _ target: CGFloat, _ rate: CGFloat = 0.12) -> CGFloat {
        let v = (smooth[key] ?? target) + (target - (smooth[key] ?? target)) * rate
        smooth[key] = v
        return v
    }

    func update(t: CGFloat, mood: Mood, level: CGFloat) {
        eyes.show(mood)
        let speak = mood == .speak, listen = mood == .listen, think = mood == .think
        let happy = mood == .happy, sleep = mood == .sleep, error = mood == .error
        let phase = CGFloat(kind.hashValue & 7)
        // Breathing and a gentle hover.
        let hop = happy ? abs(sin(t * 5.5)) * 0.12 : 0
        root.position.y = sin(t * 1.4 + phase) * (sleep ? 0.01 : 0.025) + hop
        body.scale = SCNVector3(1, 1 + sin(t * (sleep ? 0.9 : 1.6)) * 0.012, 1)
        // Head: looks around when idle, tilts to listen, looks up to think, nods while speaking.
        let yaw = ease("yaw", think ? 0.32 : listen ? 0.0 : sleep ? 0 : sin(t * 0.37 + phase) * 0.28 + sin(t * 0.91) * 0.08)
        let pitch = ease("pitch", sleep ? 0.32 : think ? -0.2 : speak ? sin(t * 4.2) * 0.05 + level * 0.06 : listen ? -0.06 : sin(t * 0.53) * 0.05)
        let roll = ease("roll", listen ? 0.24 : think ? -0.1 : happy ? sin(t * 5.5) * 0.15 : error ? sin(t * 20) * 0.04 : 0)
        neck.eulerAngles = SCNVector3(pitch, yaw, roll)
        // Eyes.
        let cycle = (t + phase).truncatingRemainder(dividingBy: 4.3)
        let blink: CGFloat = sleep || happy ? 1 : cycle < 0.14 ? max(0.06, abs(cycle - 0.07) / 0.07) : 1
        let look = CGPoint(x: ease("lx", think ? 0.05 : listen ? 0 : sin(t * 0.37 + phase) * 0.03), y: ease("ly", think ? 0.045 : 0))
        let scale = ease("es", listen ? 1.18 : speak ? 1.0 + level * 0.08 : 1)
        let glow = ease("eg", sleep ? 0.5 : listen ? 1.9 + sin(t * 8) * 0.2 : speak ? 1.3 + level * 0.9 : 1.4)
        eyes.update(blink: blink, look: look, scale: scale, glow: glow)
        // Ears perk up and light up when listening.
        let perk = ease("perk", listen ? -0.35 : sleep ? 0.3 : happy ? sin(t * 9) * 0.15 : 0)
        earL.eulerAngles.x = perk + sin(t * 2.3 + 1) * 0.04
        earR.eulerAngles.x = perk + sin(t * 2.1) * 0.04
        let twitch = (t + phase * 3).truncatingRemainder(dividingBy: 6.5) < 0.18 ? 0.18 : 0
        earR.eulerAngles.y = twitch
        for m in earLights { m.emission.intensity = ease("ear\(m.hash)", listen ? 1.6 + sin(t * 8) * 0.4 : sleep ? 0.15 : 0.5) }
        chest.emission.intensity = sleep ? 0.3 : 0.8 + 0.5 * sin(t * (speak ? 9 : 2)) + level
        // Mouth bars follow the voice.
        for (i, bar) in mouth.enumerated() {
            let wobble = 0.5 + 0.5 * sin(t * 11 + CGFloat(i) * 1.7)
            let h = speak ? 0.25 + level * (0.6 + 0.9 * wobble) * (i == 2 ? 1.2 : 1) : 0.0001
            bar.scale = SCNVector3(1, ease("m\(i)", h, 0.35), 1)
            bar.opacity = speak ? 1 : 0
        }
        beak.scale = SCNVector3(1, ease("beak", 1 + (speak ? level * 0.8 : 0), 0.35), 1)
        // Tail sways; wags when happy.
        for (i, segment) in tail.enumerated() {
            let f = CGFloat(i) / CGFloat(max(1, tail.count - 1))
            let speed: CGFloat = happy ? 7 : 1.5, amount: CGFloat = happy ? 0.22 : sleep ? 0.03 : 0.09
            let base: CGFloat = kind == .fox ? -1.45 : -1.6
            let bend: CGFloat = kind == .fox ? 0.2 : 0.24
            segment.eulerAngles = SCNVector3(i == 0 ? -0.45 : 0, 0, (i == 0 ? base : bend) + sin(t * speed - f * 2.2) * amount)
        }
        // Owl wings: a flap when happy, a small lift while listening.
        let flap = happy ? sin(t * 12) * 0.45 : listen ? 0.12 : speak ? level * 0.12 : 0.02 * sin(t * 1.6)
        wingL.eulerAngles.z = -ease("wing", flap, 0.4)
        wingR.eulerAngles.z = ease("wing2", flap, 0.4)
        // Thinking lights.
        thoughts.opacity = ease("th", think ? 1 : 0, 0.1)
        thoughts.eulerAngles.y = t * 2.4
    }
}

// MARK: - Scene

func makeScene(_ kind: Kind) -> (SCNScene, Companion) {
    let scene = SCNScene()
    scene.lightingEnvironment.contents = Look.studio()
    scene.lightingEnvironment.intensity = 1.6
    let companion = Companion(kind)
    scene.rootNode.addChildNode(companion.root)
    let camera = SCNNode()
    camera.camera = SCNCamera()
    camera.camera?.fieldOfView = 28
    camera.camera?.wantsHDR = false
    camera.position = SCNVector3(0, 0.05, 5.0)
    camera.look(at: SCNVector3(0, -0.25, 0))
    scene.rootNode.addChildNode(camera)
    let key = SCNLight(); key.type = .directional; key.intensity = 900; key.color = NSColor(srgbRed: 1, green: 0.97, blue: 0.92, alpha: 1)
    let keyNode = SCNNode(); keyNode.light = key; keyNode.eulerAngles = SCNVector3(-0.7, -0.5, 0)
    let rim = SCNLight(); rim.type = .directional; rim.intensity = 700; rim.color = Look.cyan
    let rimNode = SCNNode(); rimNode.light = rim; rimNode.eulerAngles = SCNVector3(-0.2, 2.6, 0)
    let fill = SCNLight(); fill.type = .ambient; fill.intensity = 220; fill.color = NSColor(srgbRed: 0.8, green: 0.88, blue: 1, alpha: 1)
    let fillNode = SCNNode(); fillNode.light = fill
    [keyNode, rimNode, fillNode].forEach(scene.rootNode.addChildNode)
    return (scene, companion)
}

func voiceLevel(_ t: CGFloat) -> CGFloat { max(0, 0.5 + 0.5 * sin(t * 13)) * (0.55 + 0.45 * sin(t * 2.1)) }

// MARK: - Snapshots

if let i = CommandLine.arguments.firstIndex(of: "--snapshot"), i + 1 < CommandLine.arguments.count {
    let dir = URL(fileURLWithPath: CommandLine.arguments[i + 1])
    try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    let renderer = SCNRenderer(device: MTLCreateSystemDefaultDevice(), options: nil)
    for kind in Kind.allCases {
        for mood in Mood.allCases {
            let (scene, companion) = makeScene(kind)
            scene.background.contents = NSColor(srgbRed: 0.11, green: 0.13, blue: 0.19, alpha: 1)
            renderer.scene = scene
            var t: CGFloat = 1.0
            for _ in 0..<60 { t += 1.0 / 30; companion.update(t: t, mood: mood, level: mood == .speak ? 0.8 : 0) }
            let image = renderer.snapshot(atTime: 0, with: CGSize(width: 520, height: 520), antialiasingMode: .multisampling4X)
            let rep = NSBitmapImageRep(data: image.tiffRepresentation!)!
            try! rep.representation(using: .png, properties: [:])!.write(to: dir.appendingPathComponent("\(kind.rawValue)-\(mood.rawValue).png"))
        }
    }
    print("ok")
    exit(0)
}

// MARK: - Window

final class Driver: NSObject, SCNSceneRendererDelegate {
    var companions: [Companion] = []
    var mood: Mood = .idle
    let start = Date()
    func renderer(_ renderer: SCNSceneRenderer, updateAtTime time: TimeInterval) {
        let t = CGFloat(Date().timeIntervalSince(start))
        let level = mood == .speak ? voiceLevel(t) : 0
        DispatchQueue.main.async { [self] in for c in companions { c.update(t: t, mood: mood, level: level) } }
    }
}

let app = NSApplication.shared
app.setActivationPolicy(.regular)
let driver = Driver()
let window = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 1020, height: 470), styleMask: [.titled, .closable, .miniaturizable],
                      backing: .buffered, defer: false)
window.title = "JARVIS — anteprima del compagno 3D"
let content = NSView(frame: window.contentRect(forFrameRect: window.frame))
content.wantsLayer = true
content.layer?.backgroundColor = NSColor(srgbRed: 0.11, green: 0.13, blue: 0.19, alpha: 1).cgColor
window.contentView = content
for (i, kind) in Kind.allCases.enumerated() {
    let (scene, companion) = makeScene(kind)
    let view = SCNView(frame: NSRect(x: 10 + CGFloat(i) * 335, y: 70, width: 330, height: 360))
    view.scene = scene
    view.backgroundColor = .clear
    view.antialiasingMode = .multisampling4X
    view.isPlaying = true
    view.delegate = driver
    view.preferredFramesPerSecond = 30
    content.addSubview(view)
    driver.companions.append(companion)
    let label = NSTextField(labelWithString: kind.rawValue)
    label.textColor = .white
    label.font = .systemFont(ofSize: 15, weight: .medium)
    label.frame = NSRect(x: 10 + CGFloat(i) * 335, y: 436, width: 330, height: 22)
    label.alignment = .center
    content.addSubview(label)
}
final class Buttons: NSObject {
    @objc func pick(_ sender: NSButton) { driver.mood = Mood.allCases[sender.tag] }
    @objc func background(_ sender: NSButton) {
        let colors: [NSColor] = [NSColor(srgbRed: 0.11, green: 0.13, blue: 0.19, alpha: 1), NSColor(srgbRed: 0.87, green: 0.9, blue: 0.93, alpha: 1),
                                 NSColor(srgbRed: 0.42, green: 0.33, blue: 0.55, alpha: 1)]
        content.layer?.backgroundColor = colors[sender.tag].cgColor
    }
}
let buttons = Buttons()
for (i, mood) in Mood.allCases.enumerated() {
    let b = NSButton(title: mood.rawValue, target: buttons, action: #selector(Buttons.pick(_:)))
    b.tag = i
    b.frame = NSRect(x: 10 + CGFloat(i) * 92, y: 20, width: 88, height: 30)
    content.addSubview(b)
}
for (i, name) in ["Scuro", "Chiaro", "Colorato"].enumerated() {
    let b = NSButton(title: name, target: buttons, action: #selector(Buttons.background(_:)))
    b.tag = i
    b.frame = NSRect(x: 700 + CGFloat(i) * 102, y: 20, width: 98, height: 30)
    content.addSubview(b)
}
window.center()
window.makeKeyAndOrderFront(nil)
app.activate(ignoringOtherApps: true)
app.run()
