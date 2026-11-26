import Foundation

public struct LanguageSettings: Codable, Sendable, Equatable {
    public static let defaultTargetLanguage = "German"

    public var defaultTarget: String

    enum CodingKeys: String, CodingKey {
        case defaultTarget = "default_target"
    }

    public init(defaultTarget: String = Self.defaultTargetLanguage) {
        self.defaultTarget = defaultTarget
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        let decoded = try container.decodeIfPresent(String.self, forKey: .defaultTarget)?
            .trimmingCharacters(in: .whitespacesAndNewlines)
        self.defaultTarget = (decoded?.isEmpty == false) ? decoded! : Self.defaultTargetLanguage
    }
}

public struct AppSettings: Codable, Sendable, Equatable {
    public static let defaultBaseUrl = "https://api.openai.com/v1"
    public static let defaultModel = "gpt-5-mini"
    public static let defaultHotkey = "Cmd+Option+E"

    public var baseUrl: String
    public var model: String
    public var hotkey: String
    public var language: LanguageSettings
    public var contextEnabled: [String: Bool]
    public var checkUpdatesOnStartup: Bool
    public var lastUpdateCheckUtc: String?

    enum CodingKeys: String, CodingKey {
        case baseUrl = "base_url"
        case model
        case hotkey
        case language
        case contextEnabled = "context_enabled"
        case checkUpdatesOnStartup = "check_updates_on_startup"
        case lastUpdateCheckUtc = "last_update_check_utc"
    }

    public init(
        baseUrl: String = Self.defaultBaseUrl,
        model: String = Self.defaultModel,
        hotkey: String = Self.defaultHotkey,
        language: LanguageSettings = LanguageSettings(),
        contextEnabled: [String: Bool] = [:],
        checkUpdatesOnStartup: Bool = true,
        lastUpdateCheckUtc: String? = nil
    ) {
        self.baseUrl = baseUrl
        self.model = model
        self.hotkey = hotkey
        self.language = language
        self.contextEnabled = contextEnabled
        self.checkUpdatesOnStartup = checkUpdatesOnStartup
        self.lastUpdateCheckUtc = lastUpdateCheckUtc
    }

    public init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)

        let rawBaseUrl = try container.decodeIfPresent(String.self, forKey: .baseUrl)?
            .trimmingCharacters(in: .whitespacesAndNewlines)
        self.baseUrl = (rawBaseUrl?.isEmpty == false) ? rawBaseUrl! : Self.defaultBaseUrl

        let rawModel = try container.decodeIfPresent(String.self, forKey: .model)?
            .trimmingCharacters(in: .whitespacesAndNewlines)
        self.model = (rawModel?.isEmpty == false) ? rawModel! : Self.defaultModel

        let rawHotkey = try container.decodeIfPresent(String.self, forKey: .hotkey)?
            .trimmingCharacters(in: .whitespacesAndNewlines)
        self.hotkey = (rawHotkey?.isEmpty == false) ? rawHotkey! : Self.defaultHotkey

        self.language = try container.decodeIfPresent(LanguageSettings.self, forKey: .language)
            ?? LanguageSettings()
        self.contextEnabled = try container.decodeIfPresent([String: Bool].self, forKey: .contextEnabled)
            ?? [:]
        self.checkUpdatesOnStartup = try container.decodeIfPresent(Bool.self, forKey: .checkUpdatesOnStartup)
            ?? true
        self.lastUpdateCheckUtc = try container.decodeIfPresent(String.self, forKey: .lastUpdateCheckUtc)
    }

    public static var appSupportDirectory: URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support", isDirectory: true)
        return base.appendingPathComponent("Emendly", isDirectory: true)
    }

    public static var settingsURL: URL {
        appSupportDirectory.appendingPathComponent("settings.json", isDirectory: false)
    }

    public static func load(from url: URL? = nil) -> AppSettings {
        let path = url ?? settingsURL
        guard FileManager.default.fileExists(atPath: path.path),
              let data = try? Data(contentsOf: path),
              let decoded = try? JSONDecoder().decode(AppSettings.self, from: data)
        else {
            return AppSettings()
        }
        return decoded
    }

    public func save(to url: URL? = nil) throws {
        let path = url ?? Self.settingsURL
        let dir = path.deletingLastPathComponent()
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)

        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        let data = try encoder.encode(self)
        try data.write(to: path, options: .atomic)
    }

    public func isContextEnabled(for actionId: String, defaultValue: Bool = true) -> Bool {
        contextEnabled[actionId] ?? defaultValue
    }

    public mutating func setContextEnabled(_ enabled: Bool, for actionId: String) {
        contextEnabled[actionId] = enabled
    }
}

public enum ShellLogger {
    private static let logQueue = DispatchQueue(label: "dev.emendly.logger", qos: .utility)

    public static func logAction(
        action: String,
        app: String,
        latencyMs: UInt64,
        inputBytes: Int,
        outputBytes: Int,
        status: String
    ) {
        let payload: [String: Any] = [
            "ts": isoTimestamp(),
            "event": "action",
            "action": action,
            "app": app,
            "latency_ms": latencyMs,
            "input_bytes": inputBytes,
            "output_bytes": outputBytes,
            "status": status,
        ]
        append(payload)
    }

    public static func logMeta(event: String, app: String, status: String) {
        let payload: [String: Any] = [
            "ts": isoTimestamp(),
            "event": event,
            "app": app,
            "status": status,
        ]
        append(payload)
    }

    public static func logProbe(app: String, verdict: String, strategy: String) {
        let payload: [String: Any] = [
            "ts": isoTimestamp(),
            "event": "probe",
            "app": app,
            "verdict": verdict,
            "strategy": strategy,
        ]
        append(payload)
    }

    private static func isoTimestamp() -> String {
        ISO8601DateFormatter().string(from: Date())
    }

    private static func append(_ record: [String: Any]) {
        guard JSONSerialization.isValidJSONObject(record),
              let data = try? JSONSerialization.data(withJSONObject: record),
              var line = String(data: data, encoding: .utf8)
        else {
            return
        }
        line.append("\n")

        logQueue.async {
            let dir = AppSettings.appSupportDirectory
            let logURL = dir.appendingPathComponent("emendly.log", isDirectory: false)
            do {
                try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
                if FileManager.default.fileExists(atPath: logURL.path) {
                    let handle = try FileHandle(forWritingTo: logURL)
                    defer { try? handle.close() }
                    _ = try handle.seekToEnd()
                    if let bytes = line.data(using: .utf8) {
                        try handle.write(contentsOf: bytes)
                    }
                } else {
                    try line.write(to: logURL, atomically: true, encoding: .utf8)
                }
            } catch {
                // Logging must never disrupt the shell.
            }
        }
    }
}
