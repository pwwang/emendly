import AppKit
import Foundation

public enum CaptureChain {
    @MainActor
    public static func frontmostTargetApp(excludingPid: pid_t = ProcessInfo.processInfo.processIdentifier) -> TargetAppInfo? {
        guard let app = NSWorkspace.shared.frontmostApplication,
              app.processIdentifier != excludingPid
        else {
            return nil
        }

        return TargetAppInfo(
            pid: app.processIdentifier,
            bundleIdentifier: app.bundleIdentifier ?? "",
            localizedName: app.localizedName ?? app.bundleIdentifier ?? "Unknown App"
        )
    }

    public static func capture(
        targetApp: TargetAppInfo,
        rules: AppCaptureRules,
        primaryScreenHeight: CGFloat
    ) -> CaptureSnapshot {
        let rule = rules.strategy(for: targetApp.bundleIdentifier)

        switch rule {
        case .ax:
            if let snapshot = AccessibilityCapture.capture(
                targetApp: targetApp,
                primaryScreenHeight: primaryScreenHeight
            ) {
                return snapshot
            }
            return unsupportedSnapshot(targetApp: targetApp)

        case .clipboard:
            let anchor = AccessibilityCapture.anchorRect(
                for: targetApp.pid,
                primaryScreenHeight: primaryScreenHeight
            )
            return ClipboardCapture.capture(
                targetApp: targetApp,
                forceReadOnly: false,
                allowKeys: true,
                anchorRect: anchor
            )

        case .clipboardReadOnly:
            let anchor = AccessibilityCapture.anchorRect(
                for: targetApp.pid,
                primaryScreenHeight: primaryScreenHeight
            )
            return ClipboardCapture.capture(
                targetApp: targetApp,
                forceReadOnly: true,
                allowKeys: true,
                anchorRect: anchor
            )

        case .clipboardNoKeys:
            let anchor = AccessibilityCapture.anchorRect(
                for: targetApp.pid,
                primaryScreenHeight: primaryScreenHeight
            )
            return ClipboardCapture.capture(
                targetApp: targetApp,
                forceReadOnly: false,
                allowKeys: false,
                anchorRect: anchor
            )

        case nil:
            if let snapshot = AccessibilityCapture.capture(
                targetApp: targetApp,
                primaryScreenHeight: primaryScreenHeight
            ) {
                return snapshot
            }
            let anchor = AccessibilityCapture.anchorRect(
                for: targetApp.pid,
                primaryScreenHeight: primaryScreenHeight
            )
            return ClipboardCapture.capture(
                targetApp: targetApp,
                forceReadOnly: false,
                allowKeys: true,
                anchorRect: anchor
            )
        }
    }

    public static func writeBack(
        replacement: String,
        snapshot: CaptureSnapshot,
        actionName: String,
        clipboard: ClipboardCapture
    ) async -> Bool {
        guard !replacement.isEmpty, !snapshot.isReadOnly else {
            return false
        }

        if snapshot.strategyName == CaptureStrategyKind.ax.rawValue {
            if let runningApp = NSRunningApplication(processIdentifier: snapshot.targetApp.pid),
               !runningApp.isTerminated
            {
                await MainActor.run {
                    ClipboardCapture.activateApplication(runningApp)
                }
                try? await Task.sleep(nanoseconds: 40_000_000)
            }

            if AccessibilityCapture.replaceSelectedText(replacement, pid: snapshot.targetApp.pid) {
                clipboard.recordUndo(
                    UndoRecord(
                        targetApp: snapshot.targetApp,
                        actionName: actionName,
                        previousText: snapshot.selectedText,
                        insertedText: replacement
                    )
                )
                return true
            }
        }

        return await clipboard.pasteText(
            replacement,
            into: snapshot.targetApp,
            previousText: snapshot.selectedText,
            actionName: actionName
        )
    }

    private static func unsupportedSnapshot(targetApp: TargetAppInfo) -> CaptureSnapshot {
        CaptureSnapshot(
            targetApp: targetApp,
            verdict: .unsupported,
            strategyName: "none",
            selectedText: "",
            surroundingContext: .empty,
            anchorRect: nil,
            isReadOnly: false
        )
    }
}
