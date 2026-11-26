import Foundation

public struct AppCaptureRules: Sendable, Equatable {
    public static let empty = AppCaptureRules(rules: [:])

    public static let builtInDefaults: [String: CaptureStrategyKind] = [
        "com.microsoft.vscode": .clipboard,
        "com.apple.terminal": .clipboardNoKeys,
        "com.googlecode.iterm2": .clipboardNoKeys,
        "com.apple.preview": .clipboardReadOnly,
        "dev.warp.warp-stable": .clipboardNoKeys,
    ]

    private let rules: [String: CaptureStrategyKind]

    public init(rules: [String: CaptureStrategyKind]) {
        var normalized: [String: CaptureStrategyKind] = [:]
        for (bundleID, strategy) in rules {
            let key = Self.normalizeBundleIdentifier(bundleID)
            if !key.isEmpty {
                normalized[key] = strategy
            }
        }
        self.rules = normalized
    }

    public init(rawRules: [String: String]) {
        var parsed: [String: CaptureStrategyKind] = [:]
        for (bundleID, rawStrategy) in rawRules {
            let key = Self.normalizeBundleIdentifier(bundleID)
            let strategyKey = rawStrategy.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
            if !key.isEmpty, let strategy = CaptureStrategyKind(rawValue: strategyKey) {
                parsed[key] = strategy
            }
        }
        self.rules = parsed
    }

    public static var userRulesURL: URL {
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support", isDirectory: true)
        return appSupport
            .appendingPathComponent("Emendly", isDirectory: true)
            .appendingPathComponent("app-rules.json", isDirectory: false)
    }

    public static var bundledRulesURL: URL? {
        if let url = Bundle.main.url(forResource: "app-rules", withExtension: "json") {
            return url
        }
        if let resourcePath = Bundle.main.resourcePath {
            let candidate = URL(fileURLWithPath: resourcePath).appendingPathComponent("app-rules.json")
            if FileManager.default.fileExists(atPath: candidate.path) {
                return candidate
            }
        }
        return nil
    }

    public static func loadDefault(bundledURL: URL? = nil, userURL: URL? = nil) -> AppCaptureRules {
        var merged = builtInDefaults
        if let bundled = bundledURL ?? bundledRulesURL {
            mergeFile(at: bundled, into: &merged)
        }
        mergeFile(at: userURL ?? userRulesURL, into: &merged)
        return AppCaptureRules(rules: merged)
    }

    public static func load(from url: URL) -> AppCaptureRules {
        var loaded: [String: CaptureStrategyKind] = [:]
        mergeFile(at: url, into: &loaded)
        return AppCaptureRules(rules: loaded)
    }

    public func strategy(for bundleIdentifier: String?) -> CaptureStrategyKind? {
        let key = Self.normalizeBundleIdentifier(bundleIdentifier)
        guard !key.isEmpty else { return nil }
        return rules[key]
    }

    public static func normalizeBundleIdentifier(_ bundleIdentifier: String?) -> String {
        guard let raw = bundleIdentifier else { return "" }
        return raw.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
    }

    private static func mergeFile(at url: URL, into target: inout [String: CaptureStrategyKind]) {
        guard FileManager.default.fileExists(atPath: url.path),
              let data = try? Data(contentsOf: url),
              let decoded = try? JSONDecoder().decode([String: String].self, from: data)
        else {
            return
        }

        for (bundleID, rawStrategy) in decoded {
            let key = normalizeBundleIdentifier(bundleID)
            let value = rawStrategy.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
            if !key.isEmpty, let strategy = CaptureStrategyKind(rawValue: value) {
                target[key] = strategy
            }
        }
    }
}
