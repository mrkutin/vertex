import Foundation

/// Loads/refreshes the RU CIDR list (ipdeny.com aggregated zone) and stores it
/// in the shared App Group container so the Network Extension can read it.
///
/// - App-target writes via `bootstrap()` and `refresh()`.
/// - Extension reads via `containerURL(appGroup:)` (no network access needed).
public enum RUNetsLoader {
    public static let appGroupID = "group.ru.vertices"
    public static let fileName = "ru-aggregated.zone"
    public static let sourceURL = URL(string: "https://www.ipdeny.com/ipblocks/data/aggregated/ru-aggregated.zone")!

    public enum LoaderError: LocalizedError {
        case appGroupUnavailable
        case downloadFailed(Int)
        case writeFailed(String)
        case bundledMissing

        public var errorDescription: String? {
            switch self {
            case .appGroupUnavailable: "App Group container unavailable"
            case .downloadFailed(let code): "Download failed (HTTP \(code))"
            case .writeFailed(let path): "Failed to write \(path)"
            case .bundledMissing: "Bundled ru-aggregated.zone missing"
            }
        }
    }

    /// Path inside the App Group where the zone file lives.
    /// Returns nil if the App Group container is not accessible.
    public static func containerURL() -> URL? {
        guard let dir = FileManager.default.containerURL(forSecurityApplicationGroupIdentifier: appGroupID) else {
            return nil
        }
        return dir.appendingPathComponent(fileName, isDirectory: false)
    }

    public struct Stats: Sendable, Equatable {
        public let modifiedAt: Date
        public let cidrCount: Int
        public init(modifiedAt: Date, cidrCount: Int) {
            self.modifiedAt = modifiedAt
            self.cidrCount = cidrCount
        }
    }

    /// File mtime + non-empty/non-comment line count. Returns nil if the file is missing.
    /// Counts lines without full parsing — cheap enough to call from the UI thread.
    public static func stats() -> Stats? {
        guard let url = containerURL() else { return nil }
        let path = url.path
        let fm = FileManager.default
        guard fm.fileExists(atPath: path),
              let attrs = try? fm.attributesOfItem(atPath: path),
              let mtime = attrs[.modificationDate] as? Date,
              let text = try? String(contentsOfFile: path, encoding: .utf8) else {
            return nil
        }
        var count = 0
        for line in text.split(separator: "\n", omittingEmptySubsequences: true) {
            let trimmed = line.trimmingCharacters(in: .whitespaces)
            if !trimmed.isEmpty, !trimmed.hasPrefix("#") { count += 1 }
        }
        return Stats(modifiedAt: mtime, cidrCount: count)
    }

    /// Copies bundled fallback into App Group if no file is present yet.
    /// Safe to call on every launch — no-op if file exists.
    /// `bundle` should be the App-target bundle (Bundle.main from app context).
    public static func bootstrap(bundle: Bundle) throws {
        guard let dest = containerURL() else { throw LoaderError.appGroupUnavailable }
        let fm = FileManager.default
        if fm.fileExists(atPath: dest.path) { return }
        guard let src = bundle.url(forResource: "ru-aggregated", withExtension: "zone") else {
            throw LoaderError.bundledMissing
        }
        try fm.copyItem(at: src, to: dest)
    }

    /// Downloads fresh copy from ipdeny.com and atomically replaces the App Group file.
    public static func refresh() async throws {
        guard let dest = containerURL() else { throw LoaderError.appGroupUnavailable }
        let (data, response) = try await URLSession.shared.data(from: sourceURL)
        if let http = response as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
            throw LoaderError.downloadFailed(http.statusCode)
        }
        let tmp = dest.appendingPathExtension("tmp")
        do {
            try data.write(to: tmp, options: .atomic)
            _ = try FileManager.default.replaceItemAt(dest, withItemAt: tmp)
        } catch {
            try? FileManager.default.removeItem(at: tmp)
            throw LoaderError.writeFailed(dest.path)
        }
    }
}
