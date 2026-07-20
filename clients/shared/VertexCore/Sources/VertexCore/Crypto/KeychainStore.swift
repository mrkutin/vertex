import Foundation
import Security

/// Keychain storage for identity keys and credentials.
///
/// Uses kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly so the Network Extension
/// can access keys even when the screen is locked.
public enum KeychainStore {
    private static let service = "ru.vertices"
    /// App Group ID — used as `kSecAttrAccessGroup` on iOS (where it's
    /// silently accepted) for legacy compatibility with installed iOS clients.
    /// On macOS the system rejects this as an invalid keychain access group
    /// (App Group and Keychain Access Group are different namespaces) and
    /// would prompt the user for the login keychain password every time the
    /// app touches it. macOS therefore omits the attribute and relies on the
    /// default access group, which is `$(TeamID).$(BundleID)` — already
    /// shared between the host app and the appex because they're signed by
    /// the same team. Both platforms also list `keychain-access-groups`
    /// (`$(AppIdentifierPrefix)ru.vertices`) in their entitlements, so the
    /// default group is the same group the iOS query targets in practice.
    private static let accessGroup = "group.ru.vertices"

    // MARK: - Identity Key

    private static let identityKeyAccount = "identity-key"

    /// Throws `KeychainError.notFound` if the item does not exist (genuine
    /// first launch) and `KeychainError.locked` if the device hasn't been
    /// unlocked since reboot — callers MUST distinguish these to avoid
    /// silently overwriting an existing identity key with a fresh one
    /// during the boot-to-first-unlock window.
    public static func loadIdentityKey() throws -> Data {
        try load(account: identityKeyAccount)
    }

    public static func saveIdentityKey(_ data: Data) throws {
        try save(account: identityKeyAccount, data: data)
    }

    public static func deleteIdentityKey() {
        delete(account: identityKeyAccount)
    }

    // MARK: - Password

    private static let passwordAccount = "mqtt-password"

    public static func loadPassword() throws -> String {
        let data = try load(account: passwordAccount)
        guard let str = String(data: data, encoding: .utf8) else {
            throw KeychainError.loadFailed(errSecDecode)
        }
        return str
    }

    public static func savePassword(_ password: String) throws {
        guard let data = password.data(using: .utf8) else { return }
        try save(account: passwordAccount, data: data)
    }

    public static func deletePassword() {
        delete(account: passwordAccount)
    }

    // MARK: - Generic Keychain Operations

    private static func load(account: String) throws -> Data {
        var query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        #if os(iOS)
        query[kSecAttrAccessGroup as String] = accessGroup
        #endif

        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)

        switch status {
        case errSecSuccess:
            guard let data = result as? Data else {
                throw KeychainError.loadFailed(errSecDecode)
            }
            return data
        case errSecItemNotFound:
            throw KeychainError.notFound
        case errSecInteractionNotAllowed:
            // iOS keychain is locked — happens between reboot and first
            // unlock (kSecAttrAccessibleAfterFirstUnlock* only becomes
            // readable AFTER first unlock). Distinct from notFound: the
            // item EXISTS, we just can't read it yet.
            // NOTE: errSecAuthFailed (-25293) is intentionally NOT mapped
            // to .locked — it signals an authentication / ACL failure
            // (corrupt DB, wrong keychain password) which on-demand retry
            // would loop forever on. Let it fall through to .loadFailed.
            throw KeychainError.locked
        default:
            throw KeychainError.loadFailed(status)
        }
    }

    private static func save(account: String, data: Data) throws {
        // Delete existing item first
        delete(account: account)

        var query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
        ]
        #if os(iOS)
        query[kSecAttrAccessGroup as String] = accessGroup
        #endif

        let status = SecItemAdd(query as CFDictionary, nil)
        guard status == errSecSuccess else {
            throw KeychainError.saveFailed(status)
        }
    }

    private static func delete(account: String) {
        var query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        #if os(iOS)
        query[kSecAttrAccessGroup as String] = accessGroup
        #endif
        SecItemDelete(query as CFDictionary)
    }
}

public enum KeychainError: LocalizedError, Equatable {
    case saveFailed(OSStatus)
    case loadFailed(OSStatus)
    /// Item does not exist — genuine first launch.
    case notFound
    /// Keychain is locked (errSecInteractionNotAllowed only).
    /// Happens on iOS between reboot and the user's first unlock.
    case locked

    public var errorDescription: String? {
        switch self {
        case .saveFailed(let status):
            "Keychain save failed: \(status)"
        case .loadFailed(let status):
            "Keychain load failed: \(status)"
        case .notFound:
            "Keychain item not found"
        case .locked:
            "Keychain is locked (device not yet unlocked since reboot)"
        }
    }
}
