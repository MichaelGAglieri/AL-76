import Foundation

/// Runs main-actor code from a callback that macOS delivers on the main thread (timers, Network
/// handlers on the main queue) without asking the Swift runtime which executor is current.
///
/// `MainActor.assumeIsolated` asks it, and on 12, 13 and 14 November that question itself crashed
/// JARVIS (EXC_BAD_ACCESS at 0x1e in `swift_task_isMainExecutorImpl`, from the satellite's one-second
/// timer and the iPhone bridge's timer), always after hours with the Mac in and out of sleep. Here the
/// main thread is checked with `Thread.isMainThread`, which reads no runtime state; on any other
/// thread the code is queued on the main one instead of crashing.
func onMainThread(_ body: @escaping @MainActor () -> Void) {
    if Thread.isMainThread {
        // Same calling convention: the global-actor mark is not part of the closure's layout.
        unsafeBitCast(body, to: (() -> Void).self)()
    } else {
        DispatchQueue.main.async { unsafeBitCast(body, to: (() -> Void).self)() }
    }
}
