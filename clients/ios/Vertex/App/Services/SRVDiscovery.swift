import Foundation
import VertexCore
import os

// MARK: - Models

struct SRVRecord: Codable, Comparable, Sendable {
    let priority: UInt16
    let weight: UInt16
    let port: UInt16
    let target: String

    static func < (lhs: SRVRecord, rhs: SRVRecord) -> Bool {
        if lhs.priority != rhs.priority { return lhs.priority < rhs.priority }
        return lhs.weight > rhs.weight
    }
}

struct SRVDiscoveryResult: Codable, Sendable {
    let domain: String
    var backupDomain: String?
    var brokers: [SRVRecord]
    var exits: [SRVRecord]
    /// Per-exit display name read from a TXT record on the SRV target host
    /// (e.g. `aws.exit.vertices.ru. IN TXT "Toronto, Canada"`). Key = exit
    /// ID (first label of the SRV target). Missing entry → no city/country
    /// available, UI falls back to uppercased ID via `NodeLabels.edgeLabel`.
    /// Defaulted for backward-compat with cached results from older builds.
    var exitDisplayNames: [String: String] = [:]
    var updatedAt: Date

    /// Broker URLs sorted by SRV priority. Port convention: 8883→mqtts, 443→wss, 1883→mqtt.
    var brokerURLs: [String] {
        brokers.map { record in
            switch record.port {
            case 8883: "mqtts://\(record.target):\(record.port)"
            case 443: "wss://\(record.target):\(record.port)"
            case 1883: "mqtt://\(record.target):\(record.port)"
            default: "mqtt://\(record.target):\(record.port)"
            }
        }
    }

    /// Exit IDs extracted from SRV targets. Convention: "{id}.exit.{domain}" → first label.
    var exitIDs: [String] {
        exits.map { $0.target.components(separatedBy: ".").first ?? $0.target }
    }
}

// MARK: - DoH wire types

private struct DoHResponse: Codable {
    let Status: Int
    let Answer: [DoHAnswer]?
}

private struct DoHAnswer: Codable {
    let name: String
    let type: Int
    let data: String
}

// MARK: - SRVDiscovery

final class SRVDiscovery: Sendable {
    private let log = Logger(subsystem: "ru.vertices", category: "srv")

    private static let cacheKey = "srvDiscoveryCache"
    private static let dohProviders = [
        "https://cloudflare-dns.com/dns-query",
        "https://dns.google/resolve",
    ]

    // MARK: - Public

    /// Try primary domain → cached backup domain → cached results.
    func resolveWithFallback(domain: String) async -> SRVDiscoveryResult? {
        // 1. Primary domain
        if let result = try? await resolve(domain: domain) {
            Self.saveCache(result)
            return result
        }
        log.warning("Primary domain \(domain) failed")

        // 2. Backup domain from cache
        if let cached = Self.loadCache(), let backup = cached.backupDomain, !backup.isEmpty {
            if let result = try? await resolve(domain: backup) {
                Self.saveCache(result)
                log.info("Resolved via backup \(backup)")
                return result
            }
            log.warning("Backup \(backup) also failed")
        }

        // 3. Cached results
        if let cached = Self.loadCache(), !cached.brokers.isEmpty {
            log.info("Using cached results (age: \(Int(-cached.updatedAt.timeIntervalSinceNow))s)")
            return cached
        }

        log.error("All DNS discovery failed for \(domain)")
        return nil
    }

    func resolve(domain: String) async throws -> SRVDiscoveryResult {
        async let brokerRecords = lookupSRV("_mqtt._tcp.\(domain)")
        async let exitRecords = lookupSRVSafe("_vtx-exit._tcp.\(domain)")
        async let backupRecords = lookupSRVSafe("_vtx-backup._tcp.\(domain)")

        let brokers = try await brokerRecords
        let exits = await exitRecords
        let backups = await backupRecords

        guard !brokers.isEmpty else { throw SRVError.noRecords }

        let backupDomain = backups.sorted().first.map { record in
            record.target.hasSuffix(".") ? String(record.target.dropLast()) : record.target
        }

        // TXT lookup for each exit's SRV target host. Mapped to exit ID
        // (first label) so the UI can render "Toronto, Canada" alongside
        // the "aws" code without hardcoding the city table. Each query
        // runs in parallel; failures fall back to no entry, which makes
        // `NodeLabels.edgeLabel` render the uppercased ID instead.
        let exitDisplayNames = await fetchExitDisplayNames(exits: exits)

        log.info("Resolved \(domain): \(brokers.count) brokers, \(exits.count) exits, backup=\(backupDomain ?? "none")")

        return SRVDiscoveryResult(
            domain: domain,
            backupDomain: backupDomain,
            brokers: brokers.sorted(),
            exits: exits.sorted(),
            exitDisplayNames: exitDisplayNames,
            updatedAt: Date()
        )
    }

    private func fetchExitDisplayNames(exits: [SRVRecord]) async -> [String: String] {
        await withTaskGroup(of: (String, String?).self) { group in
            for record in exits {
                let target = record.target.hasSuffix(".") ? String(record.target.dropLast()) : record.target
                let id = target.components(separatedBy: ".").first ?? target
                guard !id.isEmpty else { continue }
                group.addTask { [target] in
                    let txt = try? await self.lookupTXT(target)
                    return (id, txt)
                }
            }
            var result: [String: String] = [:]
            for await (id, txt) in group {
                if let txt, !txt.isEmpty { result[id] = txt }
            }
            return result
        }
    }

    // MARK: - DoH Lookup

    private func lookupSRVSafe(_ name: String) async -> [SRVRecord] {
        (try? await lookupSRV(name)) ?? []
    }

    private func lookupSRV(_ name: String) async throws -> [SRVRecord] {
        var lastError: Error = SRVError.noRecords
        for provider in Self.dohProviders {
            do {
                let records = try await queryDoH(provider: provider, name: name)
                if !records.isEmpty { return records }
            } catch {
                log.warning("DoH \(provider) for \(name): \(error.localizedDescription)")
                lastError = error
            }
        }
        throw lastError
    }

    /// TXT record lookup with provider failover. Returns the joined data on
    /// the first non-empty answer, or `nil` if every provider fails or the
    /// record is absent — TXT metadata is always optional, callers must
    /// tolerate missing values.
    private func lookupTXT(_ name: String) async throws -> String? {
        for provider in Self.dohProviders {
            do {
                if let txt = try await queryDoHTXT(provider: provider, name: name), !txt.isEmpty {
                    return txt
                }
            } catch {
                log.warning("DoH TXT \(provider) for \(name): \(error.localizedDescription)")
            }
        }
        return nil
    }

    private func queryDoHTXT(provider: String, name: String) async throws -> String? {
        guard var components = URLComponents(string: provider) else { return nil }
        components.queryItems = [
            URLQueryItem(name: "name", value: name),
            URLQueryItem(name: "type", value: "TXT"),
        ]
        guard let url = components.url else { return nil }

        var request = URLRequest(url: url, timeoutInterval: 5)
        request.setValue("application/dns-json", forHTTPHeaderField: "Accept")

        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, http.statusCode == 200 else { return nil }

        let doh = try JSONDecoder().decode(DoHResponse.self, from: data)
        guard doh.Status == 0, let answers = doh.Answer else { return nil }

        // DoH JSON returns TXT data RFC 1035-quoted; `TXTParser.parse`
        // un-escapes embedded quotes/backslashes and concatenates multiple
        // character-strings — see VertexCore/Util/TXTParser.swift.
        guard let answer = answers.first(where: { $0.type == 16 }) else { return nil }
        let cleaned = TXTParser.parse(answer.data).trimmingCharacters(in: .whitespaces)
        return cleaned.isEmpty ? nil : cleaned
    }

    private func queryDoH(provider: String, name: String) async throws -> [SRVRecord] {
        guard var components = URLComponents(string: provider) else {
            throw SRVError.httpError
        }
        components.queryItems = [
            URLQueryItem(name: "name", value: name),
            URLQueryItem(name: "type", value: "SRV"),
        ]
        guard let url = components.url else { throw SRVError.httpError }

        var request = URLRequest(url: url, timeoutInterval: 5)
        request.setValue("application/dns-json", forHTTPHeaderField: "Accept")

        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, http.statusCode == 200 else {
            throw SRVError.httpError
        }

        let doh = try JSONDecoder().decode(DoHResponse.self, from: data)
        guard doh.Status == 0, let answers = doh.Answer else { return [] }

        return answers.compactMap { answer -> SRVRecord? in
            guard answer.type == 33 else { return nil }
            let parts = answer.data.split(separator: " ")
            guard parts.count == 4,
                  let priority = UInt16(parts[0]),
                  let weight = UInt16(parts[1]),
                  let port = UInt16(parts[2])
            else { return nil }
            let target = String(parts[3]).trimmingCharacters(in: CharacterSet(charactersIn: ". "))
            guard !target.isEmpty else { return nil }
            return SRVRecord(priority: priority, weight: weight, port: port, target: target)
        }
    }

    // MARK: - Cache

    static func loadCache() -> SRVDiscoveryResult? {
        guard let data = UserDefaults.standard.data(forKey: cacheKey) else { return nil }
        return try? JSONDecoder().decode(SRVDiscoveryResult.self, from: data)
    }

    static func saveCache(_ result: SRVDiscoveryResult) {
        if let data = try? JSONEncoder().encode(result) {
            UserDefaults.standard.set(data, forKey: cacheKey)
        }
    }
}

enum SRVError: LocalizedError {
    case noRecords
    case httpError

    var errorDescription: String? {
        switch self {
        case .noRecords: "No SRV records found"
        case .httpError: "DNS-over-HTTPS request failed"
        }
    }
}
