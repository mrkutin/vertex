import Foundation
import MetricKit
import os

/// Subscribes to `MXMetricManager` and persists payloads as JSON files in the
/// shared App Group container so the host app and a future extension snapshot
/// can both read them.
///
/// MetricKit delivers a single `MXMetricPayload` once every 24h (when the device
/// is plugged in / on Wi-Fi). Diagnostic payloads (hangs, crashes, CPU
/// exceptions, disk writes) arrive on demand. We keep the last `keepLast`
/// files and discard the oldest.
///
/// Used purely for measurement — no behavior change to the tunnel. The host
/// app just needs to be open for at least a moment for delivery to happen.
final class MetricsCollector: NSObject, MXMetricManagerSubscriber, @unchecked Sendable {
    static let appGroupID = "group.ru.vertices"
    static let keepLast = 30

    private let log = Logger(subsystem: "ru.vertices", category: "metrics")

    override init() {
        super.init()
        MXMetricManager.shared.add(self)
        log.info("MetricsCollector subscribed to MXMetricManager")
    }

    deinit {
        MXMetricManager.shared.remove(self)
    }

    // MARK: - MXMetricManagerSubscriber

    func didReceive(_ payloads: [MXMetricPayload]) {
        log.info("Received \(payloads.count) metric payload(s)")
        for payload in payloads {
            persist(payload.jsonRepresentation(), kind: "metric", begin: payload.timeStampBegin)
        }
    }

    func didReceive(_ payloads: [MXDiagnosticPayload]) {
        log.info("Received \(payloads.count) diagnostic payload(s)")
        for payload in payloads {
            persist(payload.jsonRepresentation(), kind: "diagnostic", begin: payload.timeStampBegin)
        }
    }

    // MARK: - Storage

    /// Lists persisted payload files (newest first) for the Diagnostics UI.
    static func listPayloads() -> [URL] {
        guard let dir = payloadsDirectory() else { return [] }
        let urls = (try? FileManager.default.contentsOfDirectory(
            at: dir,
            includingPropertiesForKeys: [.contentModificationDateKey],
            options: [.skipsHiddenFiles]
        )) ?? []
        return urls.sorted { lhs, rhs in
            let l = (try? lhs.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate ?? .distantPast
            let r = (try? rhs.resourceValues(forKeys: [.contentModificationDateKey]))?.contentModificationDate ?? .distantPast
            return l > r
        }
    }

    static func payloadsDirectory() -> URL? {
        guard let container = FileManager.default.containerURL(
            forSecurityApplicationGroupIdentifier: appGroupID
        ) else { return nil }
        let dir = container.appendingPathComponent("MetricKit", isDirectory: true)
        if !FileManager.default.fileExists(atPath: dir.path) {
            try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        }
        return dir
    }

    private func persist(_ data: Data, kind: String, begin: Date) {
        guard let dir = Self.payloadsDirectory() else {
            log.error("App Group container unavailable, skipping persist")
            return
        }
        let stamp = ISO8601DateFormatter.fileSafe.string(from: begin)
        let url = dir.appendingPathComponent("\(kind)-\(stamp).json")
        do {
            try data.write(to: url, options: .atomic)
            log.info("Persisted \(kind) payload: \(url.lastPathComponent)")
        } catch {
            log.error("Persist failed: \(error.localizedDescription)")
        }
        prune(in: dir)
    }

    private func prune(in dir: URL) {
        let urls = Self.listPayloads()
        guard urls.count > Self.keepLast else { return }
        for url in urls.dropFirst(Self.keepLast) {
            try? FileManager.default.removeItem(at: url)
        }
    }
}

private extension ISO8601DateFormatter {
    /// Filesystem-safe variant: dashes only, no colons (e.g. `20260427T120530Z`).
    /// `string(from:)` is documented thread-safe — only formatOptions mutation
    /// would race, and that happens once at init.
    nonisolated(unsafe) static let fileSafe: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withYear, .withMonth, .withDay, .withTime]
        return f
    }()
}
