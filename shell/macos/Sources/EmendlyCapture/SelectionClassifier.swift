import Foundation

public enum SelectionClassifier {
    public static let maxWordLength = 64

    public static func classify(_ rawSelection: String?, isReadOnly: Bool = false) -> SelectionClassification {
        let untrimmed = rawSelection ?? ""
        guard !untrimmed.isEmpty else {
            return SelectionClassification(
                state: .empty,
                text: "",
                untrimmedText: untrimmed,
                isReadOnly: isReadOnly,
                wordCount: 0
            )
        }

        guard let firstNonWS = untrimmed.firstIndex(where: { !$0.isWhitespace }),
              let lastNonWS = untrimmed.lastIndex(where: { !$0.isWhitespace })
        else {
            return SelectionClassification(
                state: .empty,
                text: "",
                untrimmedText: untrimmed,
                isReadOnly: isReadOnly,
                wordCount: 0
            )
        }

        let afterLast = untrimmed.index(after: lastNonWS)
        let leading = String(untrimmed[..<firstNonWS])
        let trimmed = String(untrimmed[firstNonWS..<afterLast])
        let trailing = String(untrimmed[afterLast...])

        let tokens = trimmed.split(whereSeparator: { $0.isWhitespace })
        let wordCount = tokens.count

        let state: SelectionState
        if wordCount == 1 && !trimmed.contains(where: { $0.isWhitespace }) && trimmed.count <= maxWordLength {
            state = .word
        } else {
            state = .passage
        }

        return SelectionClassification(
            state: state,
            text: trimmed,
            untrimmedText: untrimmed,
            isReadOnly: isReadOnly,
            wordCount: wordCount,
            leadingWhitespace: leading,
            trailingWhitespace: trailing
        )
    }
}
