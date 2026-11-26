import Foundation
import Security

public enum ApiKeySource: String, Sendable, Equatable {
    case environment
    case keychain
    case none
}

public enum KeychainError: LocalizedError, Sendable {
    case encodingFailed
    case osStatus(OSStatus)

    public var errorDescription: String? {
        switch self {
        case .encodingFailed:
            return "Could not encode API key as UTF-8."
        case .osStatus(let status):
            if let message = SecCopyErrorMessageString(status, nil) as String? {
                return "Keychain error (\(status)): \(message)"
            }
            return "Keychain error (OSStatus \(status))."
        }
    }
}

public enum KeychainHelper {
    public static let service = "dev.emendly.api-key"
    public static let account = "EMENDLY_API_KEY"
    public static let envKeyName = "EMENDLY_API_KEY"

    public static func resolveApiKey() -> (key: String?, source: ApiKeySource) {
        if let envValue = ProcessInfo.processInfo.environment[envKeyName]?
            .trimmingCharacters(in: .whitespacesAndNewlines),
           !envValue.isEmpty
        {
            return (envValue, .environment)
        }

        if let keychainValue = readApiKey()?.trimmingCharacters(in: .whitespacesAndNewlines),
           !keychainValue.isEmpty
        {
            return (keychainValue, .keychain)
        }

        return (nil, .none)
    }

    public static func readApiKey() -> String? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]

        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        guard status == errSecSuccess,
              let data = result as? Data,
              let value = String(data: data, encoding: .utf8)
        else {
            return nil
        }

        let trimmed = value.trimmingCharacters(in: .whitespacesAndNewlines)
        return trimmed.isEmpty ? nil : trimmed
    }

    public static func saveApiKey(_ key: String) throws {
        let trimmed = key.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            try deleteApiKey()
            return
        }

        guard let data = trimmed.data(using: .utf8) else {
            throw KeychainError.encodingFailed
        }

        let baseQuery: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]

        var addQuery = baseQuery
        addQuery[kSecValueData as String] = data
        addQuery[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock

        let addStatus = SecItemAdd(addQuery as CFDictionary, nil)
        if addStatus == errSecSuccess {
            return
        }

        if addStatus == errSecDuplicateItem {
            let attributes: [String: Any] = [
                kSecValueData as String: data,
            ]
            let updateStatus = SecItemUpdate(baseQuery as CFDictionary, attributes as CFDictionary)
            guard updateStatus == errSecSuccess else {
                throw KeychainError.osStatus(updateStatus)
            }
            return
        }

        throw KeychainError.osStatus(addStatus)
    }

    public static func deleteApiKey() throws {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]

        let status = SecItemDelete(query as CFDictionary)
        guard status == errSecSuccess || status == errSecItemNotFound else {
            throw KeychainError.osStatus(status)
        }
    }
}
