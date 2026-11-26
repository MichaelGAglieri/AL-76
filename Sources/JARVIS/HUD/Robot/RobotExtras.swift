// The robottino's extras (1.30.90, user request on 26 November: "chicche fighe"): what it wears and the little
// shows it puts on. Headphones and a ring that fills while studying, a stretch at the break, confetti from the
// jets for a passed exam, accessories unlocked with the hours of study (golden antenna, cape, coloured trails),
// clothes for the weather (umbrella, sunglasses, scarf) and two poles for the sign it carries with an alert.
// Every piece is built once, hidden, and shown by `Wardrobe.update`; each one can be switched off in
// Impostazioni › Robottino.
import AppKit
import SceneKit

extension RobotKit {

/// What the robot wears now.
struct Outfit: Equatable {
    var headphones = false
    var antenna = false, cape = false, trails = false
    var umbrella = false, sunglasses = false, scarf = false
}

/// A smooth bump: 0 before `start`, up to 1 in `rise` seconds, 1 until `length - fall`, back to 0 at `length`.
static func envelope(_ since: CGFloat, length: CGFloat, rise: CGFloat = 0.45, fall: CGFloat = 0.6) -> CGFloat {
    guard since >= 0, since < length else { return 0 }
    let up = min(1, since / rise), down = min(1, (length - since) / fall)
    let k = min(up, down)
    return k * k * (3 - 2 * k)
}

static func mix(_ a: CGFloat, _ b: CGFloat, _ k: CGFloat) -> CGFloat { a + (b - a) * k }

/// A tube along a path of points (closed or not), its rings turned about `normal` (the plane of the path).
static func tube(_ points: [SCNVector3], radius: CGFloat, sides: Int = 10, normal: SIMD3<Float>) -> SCNGeometry {
    var positions: [SCNVector3] = [], normals: [SCNVector3] = [], indices: [UInt32] = []
    let p = points.map { SIMD3<Float>(Float($0.x), Float($0.y), Float($0.z)) }
    for (i, c) in p.enumerated() {
        let next = p[min(i + 1, p.count - 1)], prev = p[max(i - 1, 0)]
        let tangent = simd_normalize(next - prev)
        let side = simd_normalize(simd_cross(tangent, normal))
        let up = simd_cross(side, tangent)
        for s in 0..<sides {
            let a = Float(s) / Float(sides) * 2 * .pi
            let n = side * cos(a) + up * sin(a)
            let v = c + n * Float(radius)
            positions.append(SCNVector3(v.x, v.y, v.z))
            normals.append(SCNVector3(n.x, n.y, n.z))
        }
    }
    for i in 0..<(p.count - 1) {
        for s in 0..<sides {
            let a = UInt32(i * sides + s), b = UInt32(i * sides + (s + 1) % sides)
            let c = a + UInt32(sides), d = b + UInt32(sides)
            indices += [a, c, b, b, c, d]
        }
    }
    return SCNGeometry(sources: [SCNGeometrySource(vertices: positions), SCNGeometrySource(normals: normals)],
                       elements: [SCNGeometryElement(indices: indices, primitiveType: .triangles)])
}

static func material(_ color: NSColor, metal: CGFloat = 0, rough: CGFloat = 0.5) -> SCNMaterial {
    let m = SCNMaterial()
    m.lightingModel = .physicallyBased
    m.diffuse.contents = color
    m.metalness.contents = metal
    m.roughness.contents = rough
    return m
}

static let gold = NSColor(srgbRed: 1, green: 0.76, blue: 0.28, alpha: 1)
static let confettiPiece = image(32) { c in
    c.setFillColor(CGColor(gray: 1, alpha: 1))
    c.fill(CGRect(x: 4, y: 11, width: 24, height: 10))
}

// MARK: - The wardrobe

final class Wardrobe {
    private unowned let robot: Robot
    let headphones = SCNNode(), antenna = SCNNode(), cape = SCNNode(), umbrella = SCNNode(), sunglasses = SCNNode(), scarf = SCNNode()
    let ring = SCNNode()
    private var carrying = false
    private let antennaTip = SCNNode()
    private let ringArc = SCNNode(), ringHead = SCNNode()
    private var ringDrawn: CGFloat = -1
    private var trails: [SCNParticleSystem] = [], confetti: [SCNParticleSystem] = []
    private let scarfTail = SCNNode()
    private var poleTips: [SCNNode] = []
    /// Where the hands are, in each arm's own space (measured on the model: the fist under the forearm).
    static let hand = SCNVector3(0.17, -0.5, 0.04)

    init(robot: Robot) {
        self.robot = robot
        buildHeadphones()
        buildAntenna()
        buildCape()
        buildUmbrella()
        buildSunglasses()
        buildScarf()
        buildRing()
        buildPoles()
        buildParticles()
        for n in [headphones, antenna, cape, umbrella, sunglasses, scarf, ring] { n.isHidden = true }
        for arm in [robot.armNeg, robot.armPos] { arm.childNodes.filter { $0.name == "pole" }.forEach { $0.isHidden = true } }
    }

    // Headphones: a band over the cap from ear to ear, and two cups over the ear discs, with a cyan light.
    private func buildHeadphones() {
        robot.head.addChildNode(headphones)
        headphones.position = SCNVector3(0, -1.1, 0)   // the head's origin is at y 1.1: these are model coordinates
        let dark = material(NSColor(white: 0.13, alpha: 1), metal: 0.3, rough: 0.35)
        let pad = material(NSColor(white: 0.22, alpha: 1), rough: 0.8)
        let light = glowMaterial(cyan)
        light.lightingModel = .constant
        let cx: CGFloat = -0.011, cy: CGFloat = 1.42, z: CGFloat = -0.07
        let band = (0...40).map { i -> SCNVector3 in
            let a = CGFloat(i) / 40 * .pi * 1.04 - .pi * 0.02
            return SCNVector3(cx + cos(a) * 0.372, cy + sin(a) * 0.4, z)
        }
        let bandNode = SCNNode(geometry: tube(band, radius: 0.026, normal: SIMD3(0, 0, 1)))
        bandNode.geometry?.materials = [dark]
        headphones.addChildNode(bandNode)
        let cushion = SCNNode(geometry: SCNBox(width: 0.2, height: 0.035, length: 0.09, chamferRadius: 0.017))
        cushion.geometry?.materials = [pad]
        cushion.position = SCNVector3(cx, cy + 0.395, z)
        headphones.addChildNode(cushion)
        for side in [-1, 1] as [CGFloat] {
            let cup = SCNNode(geometry: SCNCylinder(radius: 0.105, height: 0.075))
            cup.geometry?.materials = [dark]
            cup.eulerAngles.z = .pi / 2
            cup.position = SCNVector3(cx + side * 0.362, 1.43, -0.02)
            headphones.addChildNode(cup)
            let glow = SCNNode(geometry: SCNTorus(ringRadius: 0.07, pipeRadius: 0.011))
            glow.geometry?.materials = [light]
            glow.eulerAngles.z = .pi / 2
            glow.position = SCNVector3(cx + side * 0.402, 1.43, -0.02)
            headphones.addChildNode(glow)
            let arm = SCNNode(geometry: SCNBox(width: 0.03, height: 0.08, length: 0.05, chamferRadius: 0.01))
            arm.geometry?.materials = [dark]
            arm.position = SCNVector3(cx + side * 0.37, 1.5, -0.04)
            headphones.addChildNode(arm)
        }
    }

    // Golden antenna on top of the cap, with a glowing ball that wobbles.
    private func buildAntenna() {
        robot.head.addChildNode(antenna)
        antenna.position = SCNVector3(-0.01, 1.73 - 1.1, -0.06)
        let goldM = material(gold, metal: 1, rough: 0.22)
        let base = SCNNode(geometry: SCNCylinder(radius: 0.04, height: 0.03))
        base.geometry?.materials = [goldM]
        antenna.addChildNode(base)
        antenna.addChildNode(antennaTip)
        let rod = SCNNode(geometry: SCNCylinder(radius: 0.009, height: 0.2))
        rod.geometry?.materials = [goldM]
        rod.position = SCNVector3(0, 0.1, 0)
        antennaTip.addChildNode(rod)
        let ball = SCNNode(geometry: SCNSphere(radius: 0.036))
        let shine = material(gold, metal: 0.9, rough: 0.15)
        shine.emission.contents = NSColor(srgbRed: 1, green: 0.7, blue: 0.2, alpha: 1)
        shine.emission.intensity = 0.55
        ball.geometry?.materials = [shine]
        ball.position = SCNVector3(0, 0.215, 0)
        antennaTip.addChildNode(ball)
        let halo = SCNNode(geometry: SCNPlane(width: 0.2, height: 0.2))
        halo.geometry?.materials = [glowMaterial(softDot, NSColor(srgbRed: 1, green: 0.8, blue: 0.35, alpha: 1))]
        halo.constraints = [SCNBillboardConstraint()]
        halo.opacity = 0.6
        halo.position = ball.position
        antennaTip.addChildNode(halo)
    }

    // A red cape from the shoulders behind the backpack, flaring out and waving.
    private func buildCape() {
        robot.body.addChildNode(cape)
        let plane = SCNPlane(width: 0.5, height: 0.78)
        plane.widthSegmentCount = 12
        plane.heightSegmentCount = 20
        let m = material(NSColor(srgbRed: 0.78, green: 0.08, blue: 0.12, alpha: 1), rough: 0.75)
        m.isDoubleSided = true
        m.shaderModifiers = [.geometry: """
            float k = clamp((0.39 - _geometry.position.y) / 0.78, 0.0, 1.0);
            _geometry.position.x *= 1.0 + 0.85 * k;
            _geometry.position.z -= k * k * 0.16 + sin(u_time * 3.1 + _geometry.position.y * 7.0 + _geometry.position.x * 4.0) * 0.035 * k;
            """]
        plane.materials = [m]
        let cloth = SCNNode(geometry: plane)
        cloth.position = SCNVector3(-0.01, 0.74, -0.31)
        cloth.eulerAngles.x = 0.08
        cape.addChildNode(cloth)
        let clasp = SCNNode(geometry: SCNSphere(radius: 0.03))
        clasp.geometry?.materials = [material(gold, metal: 1, rough: 0.25)]
        for side in [-1, 1] as [CGFloat] {
            let c = clasp.clone()
            c.position = SCNVector3(side * 0.19, 1.1, -0.24)
            cape.addChildNode(c)
        }
    }

    /// A dome canopy with scalloped edges between the ribs, as a mesh: `ribs` panels, polar angle up to `open`.
    static func canopy(radius: CGFloat, ribs: Int = 8, open: CGFloat = 1.05) -> SCNGeometry {
        let rings = 14, segments = ribs * 8
        var v: [SCNVector3] = [], n: [SCNVector3] = [], uv: [CGPoint] = [], idx: [UInt32] = []
        for i in 0...rings {
            for j in 0...segments {
                let az = CGFloat(j) / CGFloat(segments) * 2 * .pi
                // Between two ribs the cloth sags a little: a smaller polar angle at the edge.
                let rib = abs(cos(CGFloat(ribs) * az / 2))
                let phi = open * CGFloat(i) / CGFloat(rings) * (1 - 0.07 * (1 - rib) * CGFloat(i) / CGFloat(rings))
                let x = sin(phi) * cos(az), y = cos(phi), z = sin(phi) * sin(az)
                v.append(SCNVector3(x * radius, (y - cos(open)) * radius * 0.85, z * radius))
                n.append(SCNVector3(x, y, z))
                uv.append(CGPoint(x: CGFloat(j) / CGFloat(segments), y: CGFloat(i) / CGFloat(rings)))
            }
        }
        for i in 0..<rings {
            for j in 0..<segments {
                let a = UInt32(i * (segments + 1) + j), b = a + 1, c = a + UInt32(segments + 1), d = c + 1
                idx += [a, b, c, b, d, c]
            }
        }
        return SCNGeometry(sources: [SCNGeometrySource(vertices: v), SCNGeometrySource(normals: n), SCNGeometrySource(textureCoordinates: uv)],
                           elements: [SCNGeometryElement(indices: idx, primitiveType: .triangles)])
    }

    // The umbrella when it will rain tomorrow: an umbrella hat (26 November, the user's design after a flat striped
    // umbrella in the hand was not liked): wide, blue and white, raised over the head on a short stem clamped to the
    // cap off-centre, so the golden antenna stays free under the dome.
    private func buildUmbrella() {
        let metal = material(NSColor(white: 0.78, alpha: 1), metal: 0.9, rough: 0.3)
        robot.head.addChildNode(umbrella)
        umbrella.position = SCNVector3(Self.hatStem.x, Self.hatStem.y - 1.1, Self.hatStem.z)   // the head's origin is at y 1.1
        // Flat like a parasol: the edge above the antenna's ball, the top still inside the window.
        let radius: CGFloat = 0.64, lift: CGFloat = 0.3, open: CGFloat = 0.7
        let dome = radius * 0.85 * (1 - cos(open))
        let cloth = SCNNode(geometry: Self.canopy(radius: radius, ribs: 8, open: open))
        let m = SCNMaterial()
        m.lightingModel = .physicallyBased
        m.isDoubleSided = true
        m.roughness.contents = 0.6
        let colors = [CGColor(srgbRed: 0.1, green: 0.22, blue: 0.5, alpha: 1), CGColor(srgbRed: 0.97, green: 0.95, blue: 0.9, alpha: 1)]
        m.diffuse.contents = image(256) { c in
            for i in 0..<16 { c.setFillColor(colors[i % 2]); c.fill(CGRect(x: CGFloat(i) * 16, y: 0, width: 16, height: 256)) }
        }
        cloth.geometry?.materials = [m]
        cloth.position = SCNVector3(0, lift, 0)
        umbrella.addChildNode(cloth)
        let stem = SCNNode(geometry: SCNCylinder(radius: 0.011, height: lift + dome))
        stem.geometry?.materials = [metal]
        stem.position = SCNVector3(0, (lift + dome) / 2, 0)
        umbrella.addChildNode(stem)
        let clamp = SCNNode(geometry: SCNCylinder(radius: 0.03, height: 0.025))
        clamp.geometry?.materials = [metal]
        umbrella.addChildNode(clamp)
        let tip = SCNNode(geometry: SCNSphere(radius: 0.02))
        tip.geometry?.materials = [metal]
        tip.position = SCNVector3(0, lift + dome + 0.012, 0)
        umbrella.addChildNode(tip)
    }

    /// Where the hat's stem meets the cap (model coordinates): to the side of the antenna, towards the back.
    static let hatStem = SCNVector3(0.14, 1.69, -0.06)

    // Sunglasses in front of the screen, over the eyes.
    private func buildSunglasses() {
        robot.head.addChildNode(sunglasses)
        sunglasses.position = SCNVector3(0, -1.1, 0)
        let lenses = image(256) { c in
            let s: CGFloat = 256
            c.setFillColor(CGColor(gray: 0.05, alpha: 1))
            for x in [0.06, 0.54] as [CGFloat] {
                let r = CGRect(x: x * s, y: 0.2 * s, width: 0.4 * s, height: 0.58 * s)
                c.addPath(CGPath(roundedRect: r, cornerWidth: 0.12 * s, cornerHeight: 0.12 * s, transform: nil))
            }
            c.fillPath()
            c.fill(CGRect(x: 0.44 * s, y: 0.6 * s, width: 0.12 * s, height: 0.07 * s))
            // A shine across each lens.
            c.setFillColor(CGColor(gray: 1, alpha: 0.32))
            for x in [0.14, 0.62] as [CGFloat] {
                c.move(to: CGPoint(x: x * s, y: 0.66 * s)); c.addLine(to: CGPoint(x: (x + 0.1) * s, y: 0.72 * s))
                c.addLine(to: CGPoint(x: (x + 0.02) * s, y: 0.32 * s)); c.addLine(to: CGPoint(x: (x - 0.04) * s, y: 0.3 * s))
                c.closePath()
            }
            c.fillPath()
        }
        let m = SCNMaterial()
        m.lightingModel = .physicallyBased
        m.diffuse.contents = lenses
        m.metalness.contents = 0.6
        m.roughness.contents = 0.15
        m.transparencyMode = .aOne
        let plane = SCNNode(geometry: SCNPlane(width: 0.33, height: 0.135))
        plane.geometry?.materials = [m]
        plane.position = SCNVector3(-0.011, 1.415, 0.285)
        sunglasses.addChildNode(plane)
    }

    // A striped scarf round the neck, one end hanging in front.
    private func buildScarf() {
        robot.body.addChildNode(scarf)
        let stripes = image(128) { c in
            for i in 0..<16 {
                c.setFillColor(i % 2 == 0 ? CGColor(srgbRed: 0.85, green: 0.12, blue: 0.15, alpha: 1) : CGColor(srgbRed: 0.96, green: 0.94, blue: 0.9, alpha: 1))
                c.fill(CGRect(x: CGFloat(i) * 8, y: 0, width: 8, height: 128))
            }
        }
        let m = material(.white, rough: 0.9)
        m.diffuse.contents = stripes
        let wool = SCNTorus(ringRadius: 0.165, pipeRadius: 0.05)
        wool.materials = [m]
        let neck = SCNNode(geometry: wool)
        neck.position = SCNVector3(-0.005, 1.13, 0.01)
        neck.scale = SCNVector3(1, 0.9, 1)
        scarf.addChildNode(neck)
        let end = SCNNode(geometry: SCNBox(width: 0.09, height: 0.3, length: 0.025, chamferRadius: 0.01))
        let endM = material(.white, rough: 0.9)
        endM.diffuse.contents = stripes
        endM.diffuse.contentsTransform = SCNMatrix4MakeRotation(.pi / 2, 0, 0, 1)
        end.geometry?.materials = [endM]
        end.position = SCNVector3(0, -0.15, 0)
        scarfTail.addChildNode(end)
        scarfTail.position = SCNVector3(0.09, 1.12, 0.17)
        scarf.addChildNode(scarfTail)
    }

    // The focus ring: a faint track round the waist and a bright arc that grows with the study block.
    private func buildRing() {
        robot.root.addChildNode(ring)
        ring.position = SCNVector3(0, 0.74, 0)
        let track = SCNNode(geometry: SCNTorus(ringRadius: 0.6, pipeRadius: 0.007))
        let t = glowMaterial(cyan)
        t.lightingModel = .constant
        t.transparency = 0.18
        track.geometry?.materials = [t]
        ring.addChildNode(track)
        ring.addChildNode(ringArc)
        let head = SCNNode(geometry: SCNPlane(width: 0.16, height: 0.16))
        head.geometry?.materials = [glowMaterial(softDot, cyan)]
        head.constraints = [SCNBillboardConstraint()]
        ringHead.addChildNode(head)
        ring.addChildNode(ringHead)
    }

    private func drawRing(_ progress: CGFloat) {
        let p = max(0.004, min(1, progress))
        guard abs(p - ringDrawn) > 0.002 else { return }
        ringDrawn = p
        // From the front (towards the camera), anticlockwise seen from above.
        let steps = max(2, Int(96 * p))
        let points = (0...steps).map { i -> SCNVector3 in
            let a = .pi / 2 - CGFloat(i) / CGFloat(steps) * p * 2 * .pi
            return SCNVector3(cos(a) * 0.6, 0, sin(a) * 0.6)
        }
        let g = tube(points, radius: 0.02, sides: 8, normal: SIMD3(0, 1, 0))
        let m = SCNMaterial()
        m.lightingModel = .constant
        m.diffuse.contents = NSColor(srgbRed: 0.55, green: 0.95, blue: 1, alpha: 1)
        m.emission.contents = cyan
        g.materials = [m]
        ringArc.geometry = g
        ringHead.position = points.last!
    }

    // Two poles in the hands for the alert's sign (the sign itself is a window above, `SignPanel`).
    private func buildPoles() {
        let wood = material(NSColor(srgbRed: 0.62, green: 0.42, blue: 0.22, alpha: 1), rough: 0.8)
        for arm in [robot.armNeg, robot.armPos] {
            let holder = SCNNode()
            holder.position = SCNVector3(arm === robot.armPos ? Self.hand.x : -Self.hand.x, Self.hand.y, Self.hand.z)
            arm.addChildNode(holder)
            let pole = SCNNode(geometry: SCNCylinder(radius: 0.014, height: 0.62))
            pole.geometry?.materials = [wood]
            pole.position = SCNVector3(0, 0.24, 0)
            holder.addChildNode(pole)
            let tip = SCNNode()
            tip.position = SCNVector3(0, 0.55, 0)
            holder.addChildNode(tip)
            poleTips.append(tip)
            holder.name = "pole"
        }
    }

    /// The top of each pole in world space (for the sign window), nil when not carrying it.
    var poleTops: [SCNVector3]? { carrying ? poleTips.map { $0.presentation.worldPosition } : nil }

    private func buildParticles() {
        // On the feet too: the back jets are hidden behind the body, seen from the front.
        for t in robot.thrusters + robot.feet {
            let trail = SCNParticleSystem()
            trail.particleImage = softDot
            trail.blendMode = .additive
            trail.isLightingEnabled = false
            trail.birthRate = 0
            trail.particleLifeSpan = 1.1
            trail.particleLifeSpanVariation = 0.2
            trail.emittingDirection = SCNVector3(0, -1, 0)
            trail.spreadingAngle = 12
            trail.particleVelocity = 0.55
            trail.particleSize = 0.06
            trail.particleColorVariation = SCNVector4(0.12, 0, 0, 0)
            trail.propertyControllers = [.opacity: SCNParticlePropertyController(animation: { let a = CAKeyframeAnimation(); a.values = [0.45, 0.3, 0]; a.keyTimes = [0, 0.5, 1]; return a }())]
            t.node.addParticleSystem(trail)
            trails.append(trail)

            let c = SCNParticleSystem()
            c.particleImage = confettiPiece
            c.blendMode = .alpha
            c.isLightingEnabled = false
            c.birthRate = 0
            c.particleLifeSpan = 2.4
            c.particleLifeSpanVariation = 0.5
            c.emittingDirection = SCNVector3(0, -1, 0.3)
            c.spreadingAngle = 85
            c.particleVelocity = 1.6
            c.particleVelocityVariation = 0.6
            c.acceleration = SCNVector3(0, -0.5, 0)
            c.particleSize = 0.045
            c.particleSizeVariation = 0.01
            c.particleAngle = 0
            c.particleAngleVariation = 180
            c.particleAngularVelocity = 360
            c.particleAngularVelocityVariation = 300
            c.particleColor = NSColor(srgbRed: 1, green: 0.25, blue: 0.3, alpha: 1)
            c.particleColorVariation = SCNVector4(1, 0.2, 0.1, 0)
            c.propertyControllers = [.opacity: SCNParticlePropertyController(animation: { let a = CAKeyframeAnimation(); a.values = [1, 1, 0]; a.keyTimes = [0, 0.75, 1]; return a }())]
            t.node.addParticleSystem(c)
            confetti.append(c)
        }
    }

    /// Every frame. `power`: the jets' power now; `focus`: the ring's progress (nil hides it); `party`: time
    /// since the confetti started (negative: none).
    func update(t: CGFloat, outfit: Outfit, focus: CGFloat?, sign: CGFloat, party: CGFloat, power: CGFloat) {
        headphones.isHidden = !outfit.headphones
        antenna.isHidden = !outfit.antenna
        cape.isHidden = !outfit.cape
        sunglasses.isHidden = !outfit.sunglasses
        scarf.isHidden = !outfit.scarf
        if carrying != (sign >= 0.5) {
            carrying = sign >= 0.5
            for arm in [robot.armNeg, robot.armPos] { arm.childNodes.filter { $0.name == "pole" }.forEach { $0.isHidden = !carrying } }
        }
        umbrella.isHidden = !outfit.umbrella || carrying   // both hands on the poles

        if outfit.antenna {
            antennaTip.eulerAngles = SCNVector3(sin(t * 3.3) * 0.12, 0, sin(t * 2.6 + 1) * 0.15)
        }
        if outfit.scarf { scarfTail.eulerAngles = SCNVector3(0.15 + sin(t * 1.9) * 0.08, 0, sin(t * 1.3) * 0.12) }
        // The umbrella and the poles stay upright, whatever the arms do.
        if carrying { for arm in [robot.armNeg, robot.armPos] { arm.childNodes.filter { $0.name == "pole" }.forEach { $0.worldOrientation = robot.body.worldOrientation } } }

        if let focus {
            ring.isHidden = false
            drawRing(focus)
            ring.opacity = 0.55 + 0.45 * (0.5 + 0.5 * sin(t * 1.4))
        } else {
            ring.isHidden = true
        }

        let hue = (t * 0.25).truncatingRemainder(dividingBy: 1)
        for (i, trail) in trails.enumerated() {
            trail.birthRate = outfit.trails ? 55 * min(1.2, max(0, power)) : 0
            trail.particleColor = NSColor(hue: (hue + CGFloat(i) * 0.15).truncatingRemainder(dividingBy: 1), saturation: 1, brightness: 1, alpha: 1)
        }
        // Three puffs of confetti in the first second and a half.
        let puff = party >= 0 && party < 1.5 && party.truncatingRemainder(dividingBy: 0.5) < 0.22
        for c in confetti { c.birthRate = puff ? 180 : 0 }
    }
}

// MARK: - The sign

/// The alert's sign: a cardboard board with the text, held up on the two poles. A window of its own (like the
/// speech bubble), placed every frame on the tops of the poles.
final class SignPanel {
    let panel: NSPanel
    private let view = SignView(frame: .zero)

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

    var text: String {
        get { view.text }
        set { view.text = newValue; view.needsDisplay = true }
    }

    /// `left`, `right`: the tops of the poles on screen.
    func update(left: NSPoint, right: NSPoint, screen: NSRect) {
        let size = SignView.size(for: view.text)
        let midX = (left.x + right.x) / 2, bottom = min(left.y, right.y) - 4   // on both poles, even leaning
        var frame = NSRect(x: midX - size.width / 2, y: bottom, width: size.width, height: size.height)
        frame.origin.x = min(max(frame.minX, screen.minX + 2), screen.maxX - 2 - size.width)
        frame.origin.y = min(frame.minY, screen.maxY - size.height - 2)
        frame = frame.integral
        if frame != panel.frame {
            panel.setFrame(frame, display: false)
            view.frame = NSRect(origin: .zero, size: frame.size)
        }
        if !panel.isVisible { panel.orderFrontRegardless() }
    }

    func hide() { if panel.isVisible { panel.orderOut(nil) } }
}

final class SignView: NSView {
    var text = ""
    static let font = NSFont.systemFont(ofSize: 15, weight: .heavy)
    static let width: CGFloat = 210

    static func size(for text: String) -> NSSize {
        let r = (text as NSString).boundingRect(with: NSSize(width: width - 28, height: 200), options: [.usesLineFragmentOrigin],
                                                attributes: [.font: font])
        return NSSize(width: width, height: min(150, ceil(r.height) + 30))
    }

    override var isOpaque: Bool { false }

    override func draw(_ dirtyRect: NSRect) {
        let r = bounds.insetBy(dx: 3, dy: 3)
        let board = NSBezierPath(roundedRect: r, xRadius: 6, yRadius: 6)
        NSColor(srgbRed: 0.93, green: 0.82, blue: 0.62, alpha: 1).setFill()
        board.fill()
        NSColor(srgbRed: 0.55, green: 0.38, blue: 0.2, alpha: 1).setStroke()
        board.lineWidth = 3
        board.stroke()
        let style = NSMutableParagraphStyle()
        style.alignment = .center
        let attributes: [NSAttributedString.Key: Any] = [.font: Self.font, .foregroundColor: NSColor(srgbRed: 0.2, green: 0.12, blue: 0.06, alpha: 1),
                                                         .paragraphStyle: style]
        let inner = r.insetBy(dx: 11, dy: 10)
        let h = (text as NSString).boundingRect(with: inner.size, options: [.usesLineFragmentOrigin], attributes: attributes).height
        (text as NSString).draw(with: NSRect(x: inner.minX, y: inner.midY - h / 2, width: inner.width, height: h),
                                options: [.usesLineFragmentOrigin, .truncatesLastVisibleLine], attributes: attributes)
    }
}
}
