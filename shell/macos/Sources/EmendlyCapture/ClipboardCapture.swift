import AppKit
import ApplicationServices
import Foundation

public struct SavedPasteboardItem: Sendable {
    public let entries: [(typeRawValue: String, data: Data)]

    public init(entries: [(typeRawValue: String, data: Data)]) {
        self.entries = entries
    }
}

public struct UndoRecord: Sendable, Equatable {
    public let targetApp: TargetAppInfo
    public let actionName: String
    public let previousText: String
    public let insertedText: String
    public let timestamp: Date

    public init(
        targetApp: TargetAppInfo,
        actionName: String,
        previousText: String,
        insertedText: String,
        timestamp: Date = Date()
    ) {
        self.targetApp = targetApp
        self.actionName = actionName
        self.previousText = previousText
        self.insertedText = insertedText
        self.timestamp = timestamp
    }
}

public final class ClipboardCapture: @unchecked Sendable {
    public static let keyCodeC: CGKeyCode = 8
    public static let keyCodeV: CGKeyCode = 9
    public static let keyCodeZ: CGKeyCode = 6

    private static let copyPollIntervalNs: UInt64 = 10_000_000
    private static let copyTimeoutMs = 180
    private static let activationSettleMs = 60

    private let lock = NSLock()
    private var _lastUndo: UndoRecord?

    public init() {}

    public var lastUndo: UndoRecord? {
        lock.lock()
        defer { lock.unlock() }
        return _lastUndo
    }

    public var hasUndo: Bool {
        lastUndo != nil
    }

    public func recordUndo(_ record: UndoRecord) {
        lock.lock()
        _lastUndo = record
        lock.unlock()
    }

    public func clearUndo() {
        lock.lock()
        _lastUndo = nil
        lock.unlock()
    }

    public static func savePasteboard(_ pasteboard: NSPasteboard = .general) -> [SavedPasteboardItem] {
        guard let items = pasteboard.pasteboardItems else {
            return []
        }

        return items.compactMap { item in
            var entries: [(String, Data)] = []
            for type in item.types {
                if let data = item.data(forType: type) {
                    entries.append((type.rawValue, data))
                }
            }
            return entries.isEmpty ? nil : SavedPasteboardItem(entries: entries)
        }
    }

    public static func restorePasteboard(_ items: [SavedPasteboardItem], to pasteboard: NSPasteboard = .general) {
        pasteboard.clearContents()
        guard !items.isEmpty else {
            return
        }

        let rebuilt: [NSPasteboardItem] = items.map { saved in
            let pbItem = NSPasteboardItem()
            for (rawType, data) in saved.entries {
                pbItem.setData(data, forType: NSPasteboard.PasteboardType(rawType))
            }
            return pbItem
        }
        _ = pasteboard.writeObjects(rebuilt)
    }

    public static func copyToPasteboard(_ text: String, pasteboard: NSPasteboard = .general) {
        pasteboard.clearContents()
        _ = pasteboard.setString(text, forType: .string)
    }

    public static func capture(
        targetApp: TargetAppInfo,
        forceReadOnly: Bool = false,
        allowKeys: Bool = true,
        anchorRect: CGRect? = nil
    ) -> CaptureSnapshot {
        let strategyName: String
        if !allowKeys {
            strategyName = CaptureStrategyKind.clipboardNoKeys.rawValue
        } else if forceReadOnly {
            strategyName = CaptureStrategyKind.clipboardReadOnly.rawValue
        } else {
            strategyName = CaptureStrategyKind.clipboard.rawValue
        }

        if !allowKeys {
            let verdict: CaptureVerdict = forceReadOnly ? .readOnly : .caret
            return CaptureSnapshot(
                targetApp: targetApp,
                verdict: verdict,
                strategyName: strategyName,
                selectedText: "",
                surroundingContext: .empty,
                anchorRect: anchorRect,
                isReadOnly: forceReadOnly
            )
        }

        let pasteboard = NSPasteboard.general
        let savedItems = savePasteboard(pasteboard)
        let initialChangeCount = pasteboard.changeCount

        defer {
            restorePasteboard(savedItems, to: pasteboard)
        }

        var copiedText = ""
        if postCommandShortcut(keyCode: keyCodeC) {
            let deadline = DispatchTime.now() + .milliseconds(copyTimeoutMs)
            while DispatchTime.now() < deadline {
                if pasteboard.changeCount != initialChangeCount {
                    copiedText = pasteboard.string(forType: .string) ?? ""
                    break
                }
                Thread.sleep(forTimeInterval: 0.01)
            }
        }

        let verdict: CaptureVerdict
        if forceReadOnly {
            verdict = .readOnly
        } else if !copiedText.isEmpty {
            verdict = .selection
        } else {
            verdict = .caret
        }

        return CaptureSnapshot(
            targetApp: targetApp,
            verdict: verdict,
            strategyName: strategyName,
            selectedText: copiedText,
            surroundingContext: .empty,
            anchorRect: anchorRect,
            isReadOnly: forceReadOnly
        )
    }

    public func pasteText(
        _ text: String,
        into targetApp: TargetAppInfo,
        previousText: String = "",
        actionName: String = "",
        restoreDelayMs: Int = 180
    ) async -> Bool {
        guard !text.isEmpty else {
            return false
        }

        guard let runningApp = NSRunningApplication(processIdentifier: targetApp.pid),
              !runningApp.isTerminated
        else {
            return false
        }

        let pasteboard = NSPasteboard.general
        let savedItems = Self.savePasteboard(pasteboard)

        defer {
            Self.restorePasteboard(savedItems, to: pasteboard)
        }

        Self.copyToPasteboard(text, pasteboard: pasteboard)
        await MainActor.run {
            Self.activateApplication(runningApp)
        }

        try? await Task.sleep(nanoseconds: UInt64(Self.activationSettleMs) * 1_000_000)

        guard Self.postCommandShortcut(keyCode: Self.keyCodeV) else {
            return false
        }

        let clampedDelay = max(50, restoreDelayMs)
        try? await Task.sleep(nanoseconds: UInt64(clampedDelay) * 1_000_000)

        recordUndo(
            UndoRecord(
                targetApp: targetApp,
                actionName: actionName,
                previousText: previousText,
                insertedText: text
            )
        )
        return true
    }

    public func undoLastInsert() async -> Bool {
        guard let record = lastUndo else {
            return false
        }

        guard let runningApp = NSRunningApplication(processIdentifier: record.targetApp.pid),
              !runningApp.isTerminated
        else {
            clearUndo()
            return false
        }

        await MainActor.run {
            Self.activateApplication(runningApp)
        }
        try? await Task.sleep(nanoseconds: UInt64(Self.activationSettleMs) * 1_000_000)

        let posted = Self.postCommandShortcut(keyCode: Self.keyCodeZ)
        if posted {
            clearUndo()
        }
        return posted
    }

    @discardableResult
    public static func postCommandShortcut(keyCode: CGKeyCode) -> Bool {
        guard let source = CGEventSource(stateID: .combinedSessionState),
              let keyDown = CGEvent(keyboardEventSource: source, virtualKey: keyCode, keyDown: true),
              let keyUp = CGEvent(keyboardEventSource: source, virtualKey: keyCode, keyDown: false)
        else {
            return false
        }

        keyDown.flags = .maskCommand
        keyUp.flags = .maskCommand

        keyDown.post(tap: .cghidEventTap)
        keyUp.post(tap: .cghidEventTap)
        return true
    }

    public static func activateApplication(_ app: NSRunningApplication) {
        if #available(macOS 14.0, *) {
            _ = app.activate()
        } else {
            _ = app.activate(options: .activateIgnoringOtherApps)
        }
    }
}
