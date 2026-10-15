import Foundation
import Security

/// Minimal Keychain wrapper (generic-password items). Secrets never touch UserDefaults.
enum KeychainHelper {
    static func save(_ value: String, account: String, service: String = "com.spryzen.app") -> Bool {
        guard let data = value.data(using: .utf8) else { return false }
        delete(account: account, service: service)
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecValueData as String: data,
            kSecAttrAccessible as String: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
        ]
        return SecItemAdd(query as CFDictionary, nil) == errSecSuccess
    }

    static func load(account: String, service: String = "com.spryzen.app") -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var item: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess,
              let data = item as? Data else { return nil }
        return String(data: data, encoding: .utf8)
    }

    /// Binary blobs (e.g. .p12) are stored base64-encoded through the string API.
    static func saveData(_ data: Data, account: String, service: String = "com.spryzen.app") -> Bool {
        save(data.base64EncodedString(), account: account, service: service)
    }

    static func loadData(account: String, service: String = "com.spryzen.app") -> Data? {
        guard let b64 = load(account: account, service: service) else { return nil }
        return Data(base64Encoded: b64)
    }

    @discardableResult
    static func delete(account: String, service: String = "com.spryzen.app") -> Bool {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        let status = SecItemDelete(query as CFDictionary)
        return status == errSecSuccess || status == errSecItemNotFound
    }
}
