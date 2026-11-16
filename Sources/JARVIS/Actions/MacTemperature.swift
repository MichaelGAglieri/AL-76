import Foundation

/// Mac temperatures (12 November: "a che temperatura è il mio PC?"), without administrator rights.
/// Apple Silicon publishes its sensors as HID services; the functions are IOKit's, not in the public
/// headers, so they are looked up by name. If a macOS removes them, nil and only the thermal state is said.
enum MacTemperature {
    struct Reading {
        let chip: Double?
        let ssd: Double?
        let battery: Double?
    }

    private typealias CreateFn = @convention(c) (CFAllocator?) -> Unmanaged<AnyObject>?
    private typealias SetMatchFn = @convention(c) (AnyObject, CFDictionary) -> Int32
    private typealias CopyServicesFn = @convention(c) (AnyObject) -> Unmanaged<CFArray>?
    private typealias CopyPropertyFn = @convention(c) (AnyObject, CFString) -> Unmanaged<AnyObject>?
    private typealias CopyEventFn = @convention(c) (AnyObject, Int64, Int32, Int64) -> Unmanaged<AnyObject>?
    private typealias FloatFn = @convention(c) (AnyObject, Int32) -> Double

    private static let temperatureEvent: Int64 = 15

    static func read() -> Reading? {
        guard let kit = dlopen("/System/Library/Frameworks/IOKit.framework/IOKit", RTLD_NOW) else { return nil }
        func symbol<T>(_ name: String) -> T? { dlsym(kit, name).map { unsafeBitCast($0, to: T.self) } }
        guard let create: CreateFn = symbol("IOHIDEventSystemClientCreate"),
              let setMatching: SetMatchFn = symbol("IOHIDEventSystemClientSetMatching"),
              let copyServices: CopyServicesFn = symbol("IOHIDEventSystemClientCopyServices"),
              let copyProperty: CopyPropertyFn = symbol("IOHIDServiceClientCopyProperty"),
              let copyEvent: CopyEventFn = symbol("IOHIDServiceClientCopyEvent"),
              let floatValue: FloatFn = symbol("IOHIDEventGetFloatValue"),
              let client = create(kCFAllocatorDefault)?.takeRetainedValue() else { return nil }
        // Usage page 0xff00, usage 5: the temperature sensors.
        _ = setMatching(client, ["PrimaryUsagePage": 0xff00, "PrimaryUsage": 5] as CFDictionary)
        guard let services = copyServices(client)?.takeRetainedValue() as? [AnyObject] else { return nil }
        var chip: [Double] = [], ssd: [Double] = [], battery: [Double] = []
        for service in services {
            guard let name = copyProperty(service, "Product" as CFString)?.takeRetainedValue() as? String,
                  let event = copyEvent(service, temperatureEvent, 0, 0)?.takeRetainedValue() else { continue }
            let value = floatValue(event, Int32(temperatureEvent << 16))
            guard value > 0, value < 130 else { continue }   // unused sensors read negative values
            if name.contains("tdie") { chip.append(value) }
            else if name.contains("NAND") { ssd.append(value) }
            else if name.contains("battery") { battery.append(value) }
        }
        guard !chip.isEmpty || !ssd.isEmpty || !battery.isEmpty else { return nil }
        return Reading(chip: chip.max(), ssd: ssd.max(), battery: battery.max())
    }

    /// The thermal state macOS uses to slow the chip down.
    static var thermalState: String {
        switch ProcessInfo.processInfo.thermalState {
        case .nominal: "normale"
        case .fair: "leggermente caldo"
        case .serious: "caldo: macOS rallenta il processore"
        case .critical: "critico: macOS rallenta molto il processore"
        @unknown default: "sconosciuto"
        }
    }

    static func spoken() -> String {
        var parts: [String] = []
        if let r = read() {
            if let chip = r.chip { parts.append("processore \(Int(chip.rounded())) °C") }
            if let ssd = r.ssd { parts.append("disco \(Int(ssd.rounded())) °C") }
            if let battery = r.battery { parts.append("batteria \(Int(battery.rounded())) °C") }
        }
        let state = "stato termico \(thermalState)"
        return parts.isEmpty ? "Temperature non leggibili; \(state)" : parts.joined(separator: ", ") + "; \(state)"
    }
}
