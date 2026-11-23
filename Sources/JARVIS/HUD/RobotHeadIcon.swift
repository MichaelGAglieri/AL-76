import AppKit

/// AL-76's menu-bar icon (1.30.81, user choice "D occhi ciano" of 23 November): the robot's head stylized,
/// orange helmet with the ridged band, black screen with the HUD's cyan eyes and the little "v" mouth,
/// two fins behind. In colour, not a template: it reads the same on a light and a dark menu bar.
/// Preview of the variants: tests/plancia/preview.sh.
enum RobotHeadIcon {
    private static let orange = CGColor(srgbRed: 1.0, green: 0.36, blue: 0.10, alpha: 1)
    private static let orangeLight = CGColor(srgbRed: 1.0, green: 0.55, blue: 0.32, alpha: 1)
    private static let orangeDark = CGColor(srgbRed: 0.62, green: 0.18, blue: 0.04, alpha: 1)
    private static let screen = CGColor(srgbRed: 0.06, green: 0.06, blue: 0.09, alpha: 1)
    private static let cyan = CGColor(srgbRed: 0.31, green: 0.86, blue: 1.0, alpha: 1)

    /// `size` points square; the drawing is on an 18-point grid, y up.
    static func image(size: CGFloat = 18) -> NSImage {
        let image = NSImage(size: NSSize(width: size, height: size), flipped: false) { _ in
            guard let c = NSGraphicsContext.current?.cgContext else { return false }
            c.scaleBy(x: size / 18, y: size / 18)
            // Fins, behind the helmet.
            c.setFillColor(orangeDark)
            for side in [-1.0, 1.0] as [CGFloat] {
                var t = CGAffineTransform(translationX: 9 + side * 6.3, y: 10.2).rotated(by: side * -0.12)
                c.addPath(CGPath(roundedRect: CGRect(x: -0.8, y: -3.2, width: 1.6, height: 6.6), cornerWidth: 0.8, cornerHeight: 0.8, transform: &t))
            }
            c.fillPath()
            // Helmet and the band's ridges.
            c.setFillColor(orange)
            c.addPath(CGPath(roundedRect: CGRect(x: 3.4, y: 2.4, width: 11.2, height: 12.2), cornerWidth: 4.4, cornerHeight: 4.4, transform: nil))
            c.fillPath()
            c.setFillColor(orangeLight)
            for x in [6.2, 8.45, 10.7] as [CGFloat] { c.fill(CGRect(x: x, y: 11.9, width: 1.1, height: 2.6)) }
            // Screen with the face.
            c.setFillColor(screen)
            c.addPath(CGPath(roundedRect: CGRect(x: 4.9, y: 3.9, width: 8.2, height: 7.0), cornerWidth: 2.6, cornerHeight: 2.6, transform: nil))
            c.fillPath()
            c.setFillColor(cyan)
            c.setStrokeColor(cyan)
            for x in [7.15, 10.85] as [CGFloat] { c.fillEllipse(in: CGRect(x: x - 1.0, y: 7.9 - 1.25, width: 2.0, height: 2.5)) }
            c.setLineWidth(0.8); c.setLineCap(.round); c.setLineJoin(.round)
            c.move(to: CGPoint(x: 8.2, y: 6.0)); c.addLine(to: CGPoint(x: 9, y: 5.4)); c.addLine(to: CGPoint(x: 9.8, y: 6.0))
            c.strokePath()
            return true
        }
        image.isTemplate = false
        image.accessibilityDescription = "AL-76"
        return image
    }
}
