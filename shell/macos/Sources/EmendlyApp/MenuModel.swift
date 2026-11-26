import EmendlyCapture
import Foundation

public enum ActionKind: String, Sendable, Equatable {
    case replace
    case insert
    case referenceList
    case custom
}

public enum ActionOptionKind: String, Sendable, Equatable {
    case none
    case context
    case language
}

public struct MenuAction: Identifiable, Sendable, Equatable, Hashable {
    public let id: String
    public let label: String
    public let description: String
    public let kind: ActionKind
    public let options: ActionOptionKind

    public init(
        id: String,
        label: String,
        description: String,
        kind: ActionKind,
        options: ActionOptionKind
    ) {
        self.id = id
        self.label = label
        self.description = description
        self.kind = kind
        self.options = options
    }
}

public struct MenuGroup: Identifiable, Sendable, Equatable {
    public var id: String { label }
    public let label: String
    public let items: [MenuAction]

    public init(label: String, items: [MenuAction]) {
        self.label = label
        self.items = items
    }
}

public enum MenuModel {
    public static let proofread = MenuAction(
        id: "proofread",
        label: "Proofread",
        description: "Fix spelling, grammar, and punctuation",
        kind: .replace,
        options: .none
    )

    public static let rewrite = MenuAction(
        id: "rewrite",
        label: "Rewrite",
        description: "Improve clarity and flow",
        kind: .replace,
        options: .context
    )

    public static let humanize = MenuAction(
        id: "humanize",
        label: "Humanize",
        description: "Make phrasing read more naturally",
        kind: .replace,
        options: .none
    )

    public static let promptOptimize = MenuAction(
        id: "prompt_optimize",
        label: "Prompt Optimize",
        description: "Clarify a prompt for more reliable results",
        kind: .replace,
        options: .none
    )

    public static let translate = MenuAction(
        id: "translate",
        label: "Translate",
        description: "Translate to target language",
        kind: .replace,
        options: .language
    )

    public static let synonyms = MenuAction(
        id: "synonyms",
        label: "Synonyms",
        description: "List up to five concise synonyms",
        kind: .referenceList,
        options: .none
    )

    public static let antonyms = MenuAction(
        id: "antonyms",
        label: "Antonyms",
        description: "List up to five concise antonyms",
        kind: .referenceList,
        options: .none
    )

    public static let compose = MenuAction(
        id: "compose",
        label: "Compose",
        description: "Draft new text from a brief",
        kind: .insert,
        options: .context
    )

    public static let custom = MenuAction(
        id: "custom",
        label: "Custom Prompt…",
        description: "Run a free-text instruction",
        kind: .custom,
        options: .none
    )

    public static let allActions: [MenuAction] = [
        proofread,
        rewrite,
        humanize,
        promptOptimize,
        translate,
        synonyms,
        antonyms,
        compose,
        custom,
    ]

    private static let emptyDefaults: [MenuAction] = [compose, custom]
    private static let wordDefaults: [MenuAction] = [proofread, translate, synonyms]
    private static let passageDefaults: [MenuAction] = [
        proofread,
        rewrite,
        translate,
        humanize,
        promptOptimize,
    ]

    public static let expandedGroups: [MenuGroup] = [
        MenuGroup(label: "Improve", items: [proofread, rewrite, humanize]),
        MenuGroup(label: "Change", items: [translate, promptOptimize]),
        MenuGroup(label: "Words", items: [synonyms, antonyms]),
        MenuGroup(label: "Create", items: [compose, custom]),
    ]

    public static let defaultLanguages: [String] = [
        "German",
        "English",
        "Spanish",
        "French",
        "Italian",
        "Portuguese",
        "Dutch",
        "Polish",
        "Russian",
        "Chinese (Simplified)",
        "Chinese (Traditional)",
        "Japanese",
        "Korean",
        "Arabic",
        "Hindi",
        "Turkish",
        "Swedish",
        "Norwegian",
        "Danish",
        "Finnish",
        "Czech",
        "Greek",
        "Hebrew",
        "Vietnamese",
        "Thai",
        "Indonesian",
        "Ukrainian",
    ]

    private static let languageCodes: [String: String] = [
        "german": "DE",
        "english": "EN",
        "spanish": "ES",
        "french": "FR",
        "italian": "IT",
        "portuguese": "PT",
        "dutch": "NL",
        "polish": "PL",
        "russian": "RU",
        "chinese (simplified)": "ZH",
        "chinese (traditional)": "ZH",
        "japanese": "JA",
        "korean": "KO",
        "arabic": "AR",
        "hindi": "HI",
        "turkish": "TR",
        "swedish": "SV",
        "norwegian": "NO",
        "danish": "DA",
        "finnish": "FI",
        "czech": "CS",
        "greek": "EL",
        "hebrew": "HE",
        "vietnamese": "VI",
        "thai": "TH",
        "indonesian": "ID",
        "ukrainian": "UK",
    ]

    public static func defaultActions(for state: SelectionState) -> [MenuAction] {
        switch state {
        case .empty:
            return emptyDefaults
        case .word:
            return wordDefaults
        case .passage:
            return passageDefaults
        }
    }

    public static func filterExpanded(prefix: String?) -> [MenuGroup] {
        let trimmed = prefix?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        guard !trimmed.isEmpty else {
            return expandedGroups
        }

        let lower = trimmed.lowercased()
        return expandedGroups.compactMap { group in
            let matches = group.items.filter {
                $0.label.lowercased().hasPrefix(lower) || $0.id.lowercased().hasPrefix(lower)
            }
            return matches.isEmpty ? nil : MenuGroup(label: group.label, items: matches)
        }
    }

    public static func find(id: String) -> MenuAction? {
        allActions.first(where: { $0.id == id })
    }

    public static func languageCode(for language: String?) -> String {
        let trimmed = language?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        guard !trimmed.isEmpty else { return "??" }

        if let code = languageCodes[trimmed.lowercased()] {
            return code
        }

        let letters = trimmed.filter { $0.isLetter }.prefix(2).uppercased()
        return letters.isEmpty ? "??" : letters
    }

    public static func languages(including current: String?) -> [String] {
        let trimmed = current?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        guard !trimmed.isEmpty else {
            return defaultLanguages
        }
        if defaultLanguages.contains(where: { $0.caseInsensitiveCompare(trimmed) == .orderedSame }) {
            return defaultLanguages
        }
        return [trimmed] + defaultLanguages
    }
}
