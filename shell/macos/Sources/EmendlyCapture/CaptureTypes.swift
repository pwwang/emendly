import CoreGraphics
import Foundation

public enum SelectionState: String, Sendable, Equatable, CaseIterable {
    case empty
    case word
    case passage
}

public struct SelectionClassification: Sendable, Equatable {
    public let state: SelectionState
    public let text: String
    public let untrimmedText: String
    public let isReadOnly: Bool
    public let wordCount: Int
    public let leadingWhitespace: String
    public let trailingWhitespace: String

    public init(
        state: SelectionState,
        text: String,
        untrimmedText: String? = nil,
        isReadOnly: Bool,
        wordCount: Int,
        leadingWhitespace: String = "",
        trailingWhitespace: String = ""
    ) {
        self.state = state
        self.text = text
        self.untrimmedText = untrimmedText ?? text
        self.isReadOnly = isReadOnly
        self.wordCount = wordCount
        self.leadingWhitespace = leadingWhitespace
        self.trailingWhitespace = trailingWhitespace
    }

    public func restoreSurroundingWhitespace(_ replacement: String) -> String {
        guard !replacement.isEmpty, state != .empty else {
            return replacement
        }

        var result = replacement
        if !leadingWhitespace.isEmpty, !result.hasPrefix(leadingWhitespace) {
            result = leadingWhitespace + result
        }
        if !trailingWhitespace.isEmpty, !result.hasSuffix(trailingWhitespace) {
            result += trailingWhitespace
        }
        return result
    }
}

public struct TargetAppInfo: Sendable, Equatable {
    public let pid: pid_t
    public let bundleIdentifier: String
    public let localizedName: String

    public init(pid: pid_t, bundleIdentifier: String, localizedName: String) {
        self.pid = pid
        self.bundleIdentifier = bundleIdentifier
        self.localizedName = localizedName
    }
}

public enum CaptureVerdict: String, Sendable, Equatable {
    case selection
    case caret
    case readOnly = "read_only"
    case unsupported
}

public struct SurroundingContext: Sendable, Equatable {
    public let before: String
    public let after: String
    public let fieldText: String

    public static let empty = SurroundingContext(before: "", after: "", fieldText: "")

    public init(before: String, after: String, fieldText: String) {
        self.before = before
        self.after = after
        self.fieldText = fieldText
    }

    public var hasContext: Bool {
        !before.isEmpty || !after.isEmpty
    }
}

public enum CaptureStrategyKind: String, Sendable, Equatable, CaseIterable {
    case ax = "ax"
    case clipboard = "clipboard"
    case clipboardReadOnly = "clipboard-ro"
    case clipboardNoKeys = "clipboard-nokeys"
}

public struct CaptureSnapshot: Sendable, Equatable {
    public let targetApp: TargetAppInfo
    public let verdict: CaptureVerdict
    public let strategyName: String
    public let selectedText: String
    public let surroundingContext: SurroundingContext
    public let anchorRect: CGRect?
    public let isReadOnly: Bool

    public init(
        targetApp: TargetAppInfo,
        verdict: CaptureVerdict,
        strategyName: String,
        selectedText: String,
        surroundingContext: SurroundingContext = .empty,
        anchorRect: CGRect? = nil,
        isReadOnly: Bool = false
    ) {
        self.targetApp = targetApp
        self.verdict = verdict
        self.strategyName = strategyName
        self.selectedText = selectedText
        self.surroundingContext = surroundingContext
        self.anchorRect = anchorRect
        self.isReadOnly = isReadOnly
    }

    public var classification: SelectionClassification {
        SelectionClassifier.classify(selectedText, isReadOnly: isReadOnly)
    }
}
