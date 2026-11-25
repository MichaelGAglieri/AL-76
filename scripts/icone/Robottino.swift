import AppKit

/// App icon "Robottino" (25 November, the default since 1.30.86): the ROBOTTINO's head, front view, on a
/// sky-blue tile. Same head as the menu-bar icon (HUD/RobotHeadIcon.swift) with the 3D model's details:
/// helmet with visor brim, two pale stripes, fins, black screen with eyes and smile (no star, no ear discs: user, 25 November).
/// Drawn on a 1024 grid, y up. `ios`: full square, no margin or shadow (iOS rounds it).
enum RobottinoIcon {
    static func rgb(_ r: Double, _ g: Double, _ b: Double, _ a: Double = 1) -> CGColor {
        CGColor(srgbRed: r / 255, green: g / 255, blue: b / 255, alpha: a)
    }
    static let space = CGColorSpace(name: CGColorSpace.sRGB)!
    static func gradient(_ colors: [CGColor], _ locations: [CGFloat]) -> CGGradient {
        CGGradient(colorsSpace: space, colors: colors as CFArray, locations: locations)!
    }

    static func draw(in c: CGContext, ios: Bool) {
        // Tile.
        let tile = ios ? CGPath(rect: CGRect(x: 0, y: 0, width: 1024, height: 1024), transform: nil)
            : CGPath(roundedRect: CGRect(x: 100, y: 100, width: 824, height: 824), cornerWidth: 185, cornerHeight: 185, transform: nil)
        c.saveGState()
        if !ios { c.setShadow(offset: CGSize(width: 0, height: -12), blur: 28, color: CGColor(gray: 0, alpha: 0.45)) }
        c.addPath(tile); c.setFillColor(rgb(10, 16, 34)); c.fillPath()
        c.restoreGState()
        c.saveGState()
        c.addPath(tile); c.clip()
        c.drawLinearGradient(gradient([rgb(140, 228, 255), rgb(64, 160, 240), rgb(34, 92, 200)], [0, 0.55, 1]),
                             start: CGPoint(x: 512, y: 924), end: CGPoint(x: 512, y: 100), options: [])
        c.drawRadialGradient(gradient([rgb(255, 255, 255, 0.35), rgb(255, 255, 255, 0)], [0, 1]),
                             startCenter: CGPoint(x: 512, y: 560), startRadius: 0,
                             endCenter: CGPoint(x: 512, y: 560), endRadius: 380, options: [])
        drawHead(in: c)
        // Light from above on the whole tile.
        c.drawLinearGradient(gradient([CGColor(gray: 1, alpha: 0.10), CGColor(gray: 1, alpha: 0)], [0, 1]),
                             start: CGPoint(x: 512, y: 924), end: CGPoint(x: 512, y: 640), options: [])
        c.restoreGState()
    }

    /// The head traced from an orthographic front view of the 3D model (25 November: the earlier ones copied a
    /// three-quarter view and the shape came out wrong). Coordinates are those of the 800-pixel front render
    /// (y down), mapped onto the icon.
    static let k: CGFloat = 0.926, base: CGFloat = 297
    static func pt(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: 512 + (x - 400) * k, y: base + (630 - y) * k) }
    static func rect(_ x0: CGFloat, _ y0: CGFloat, _ x1: CGFloat, _ y1: CGFloat) -> CGRect {
        let a = pt(x0, y1), b = pt(x1, y0)
        return CGRect(x: a.x, y: a.y, width: b.x - a.x, height: b.y - a.y)
    }

    /// A smooth closed outline through points given for the right half (model coordinates, top to bottom),
    /// mirrored around x = 400: the silhouette measured row by row on the front render of the model.
    static func outline(_ right: [(CGFloat, CGFloat)]) -> CGPath {
        let half = right.map { pt($0.0, $0.1) }
        let mirrored = right.reversed().dropFirst().dropLast().map { pt(800 - $0.0, $0.1) }
        let p = half + mirrored
        let path = CGMutablePath()
        let n = p.count
        path.move(to: p[0])
        for i in 0..<n {
            let p0 = p[(i - 1 + n) % n], p1 = p[i], p2 = p[(i + 1) % n], p3 = p[(i + 2) % n]
            let c1 = CGPoint(x: p1.x + (p2.x - p0.x) / 6, y: p1.y + (p2.y - p0.y) / 6)
            let c2 = CGPoint(x: p2.x - (p3.x - p1.x) / 6, y: p2.y - (p3.y - p1.y) / 6)
            path.addCurve(to: p2, control1: c1, control2: c2)
        }
        path.closeSubpath()
        return path
    }

    /// Head outline measured on the front render (800 px, y down): dome on top, widest at the eyes,
    /// tapering cheeks and a flat chin.
    static let headRight: [(CGFloat, CGFloat)] = [
        (400, 133), (495, 140), (535, 150), (564, 160), (599, 180), (630, 210), (648, 240), (660, 270),
        (667, 300), (673, 330), (676, 360), (677, 420), (676, 460), (671, 500), (662, 540), (651, 580),
        (637, 620), (627, 640), (613, 652), (595, 664), (570, 673), (520, 678), (400, 679),
    ]
    /// The screen, same way; its top is under the visor.
    static let screenRight: [(CGFloat, CGFloat)] = [
        (400, 318), (540, 320), (580, 332), (591, 380), (591, 460), (587, 495), (580, 525), (568, 552),
        (553, 571), (525, 583), (470, 591), (400, 594),
    ]

    static func drawHead(in c: CGContext) {
        let orange = rgb(255, 96, 18), orangeLight = rgb(255, 150, 84), orangeDark = rgb(204, 58, 8)
        // Shadow under the head.
        let below = pt(400, 700)
        c.drawRadialGradient(gradient([CGColor(gray: 0, alpha: 0.35), CGColor(gray: 0, alpha: 0)], [0, 1]),
                             startCenter: below, startRadius: 0, endCenter: below, endRadius: 250, options: [.drawsAfterEndLocation])
        // Fins: two thin straight blades beside the helmet.
        for x in [98.0, 702.0] as [CGFloat] {
            let r = rect(x - 8, 155, x + 8, 490)
            let fin = CGPath(roundedRect: r, cornerWidth: 7, cornerHeight: 7, transform: nil)
            c.saveGState()
            c.addPath(fin); c.clip()
            c.drawLinearGradient(gradient([orangeLight, orange, orangeDark], [0, 0.45, 1]),
                                 start: CGPoint(x: r.minX, y: 0), end: CGPoint(x: r.maxX, y: 0), options: [])
            c.restoreGState()
        }
        // Helmet: the measured silhouette.
        let helmet = outline(headRight)
        let helmetRect = helmet.boundingBox
        c.saveGState()
        c.addPath(helmet); c.clip()
        c.drawLinearGradient(gradient([orangeLight, orange, orange, orangeDark], [0, 0.3, 0.7, 1]),
                             start: CGPoint(x: helmetRect.minX, y: helmetRect.maxY), end: CGPoint(x: helmetRect.maxX, y: helmetRect.minY), options: [])
        // Two pale stripes over the dome, down to the visor.
        c.setFillColor(CGColor(gray: 1, alpha: 0.40))
        c.fill(rect(316, 120, 390, 280)); c.fill(rect(410, 120, 484, 280))
        // Lighter top left, darker edges all round: the roundness of the shell.
        c.drawRadialGradient(gradient([CGColor(gray: 1, alpha: 0.18), CGColor(gray: 1, alpha: 0)], [0, 1]),
                             startCenter: pt(330, 200), startRadius: 0, endCenter: pt(330, 200), endRadius: 160, options: [])
        c.setStrokeColor(rgb(150, 40, 0, 0.35)); c.setLineWidth(22); c.addPath(helmet); c.strokePath()
        c.restoreGState()
        // Screen: large, the whole face.
        let screen = outline(screenRight)
        let screenRect = screen.boundingBox
        c.saveGState()
        c.addPath(screen); c.clip()
        c.drawLinearGradient(gradient([rgb(40, 42, 48), rgb(14, 15, 18)], [0, 1]),
                             start: CGPoint(x: 512, y: screenRect.maxY), end: CGPoint(x: 512, y: screenRect.minY), options: [])
        c.restoreGState()
        c.setStrokeColor(rgb(120, 30, 0, 0.55)); c.setLineWidth(8); c.addPath(screen); c.strokePath()
        // Face: round eyes, a small soft smile.
        let eye = rgb(252, 252, 255)
        c.saveGState()
        c.setShadow(offset: .zero, blur: 10, color: CGColor(gray: 1, alpha: 0.35))
        c.setFillColor(eye)
        for x in [290.0, 510.0] as [CGFloat] { c.fillEllipse(in: rect(x - 33, 397, x + 33, 463)) }
        c.setStrokeColor(eye); c.setLineWidth(14); c.setLineCap(.round); c.setLineJoin(.round)
        c.move(to: pt(370, 476)); c.addQuadCurve(to: pt(430, 476), control: pt(400, 514))
        c.strokePath()
        c.restoreGState()
        // Visor: a brim bent round the head, so both its edges arch up in the middle (model: top 255 in
        // the middle and 300 at the sides, bottom 328 and 380).
        let visor = CGMutablePath()
        visor.move(to: pt(185, 318))
        visor.addQuadCurve(to: pt(212, 288), control: pt(185, 292))
        visor.addQuadCurve(to: pt(588, 288), control: pt(400, 222))
        visor.addQuadCurve(to: pt(615, 318), control: pt(615, 292))
        visor.addLine(to: pt(615, 360))
        visor.addQuadCurve(to: pt(590, 382), control: pt(615, 382))
        visor.addQuadCurve(to: pt(210, 382), control: pt(400, 276))
        visor.addQuadCurve(to: pt(185, 360), control: pt(185, 382))
        visor.closeSubpath()
        c.saveGState()
        c.setShadow(offset: CGSize(width: 0, height: -14), blur: 18, color: CGColor(gray: 0, alpha: 0.5))
        c.addPath(visor); c.setFillColor(orange); c.fillPath()
        c.restoreGState()
        c.saveGState()
        c.addPath(visor); c.clip()
        let vr = visor.boundingBox
        c.drawLinearGradient(gradient([orangeLight, orange, orange, orangeDark], [0, 0.35, 0.75, 1]),
                             start: CGPoint(x: 512, y: vr.maxY), end: CGPoint(x: 512, y: vr.minY), options: [])
        c.setStrokeColor(CGColor(gray: 1, alpha: 0.45)); c.setLineWidth(6); c.setLineCap(.round)
        c.move(to: pt(222, 312)); c.addQuadCurve(to: pt(578, 312), control: pt(400, 252)); c.strokePath()
        c.restoreGState()
    }
}
