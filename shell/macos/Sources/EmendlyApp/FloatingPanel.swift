import AppKit
import SwiftUI

@MainActor
public final class FloatingPanel: NSPanel {
    public static let defaultWidth: CGFloat = 400
    public static let defaultHeight: CGFloat = 340

    private var keyMonitor: Any?
    private var keyHandler: ((NSEvent, Bool) -> Bool)?

    public init<Content: View>(
        contentView rootView: Content,
        onKeyEvent: @escaping (NSEvent, Bool) -> Bool
    ) {
        self.keyHandler = onKeyEvent

        super.init(
            contentRect: NSRect(x: 0, y: 0, width: Self.defaultWidth, height: Self.defaultHeight),
            styleMask: [.nonactivatingPanel, .borderless, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )

        self.level = .floating
        self.isFloatingPanel = true
        self.hidesOnDeactivate = false
        self.isOpaque = false
        self.backgroundColor = .clear
        self.hasShadow = true
        self.isMovableByWindowBackground = true
        self.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .transient]
        self.appearance = NSAppearance(named: .darkAqua)
        self.isReleasedWhenClosed = false

        let hostingView = NSHostingView(rootView: rootView)
        hostingView.wantsLayer = true
        hostingView.layer?.backgroundColor = NSColor.clear.cgColor
        self.contentView = hostingView
    }

    public override var canBecomeKey: Bool { true }
    public override var canBecomeMain: Bool { false }

    public override func cancelOperation(_ sender: Any?) {
        let syntheticEscape = NSEvent.keyEvent(
            with: .keyDown,
            location: .zero,
            modifierFlags: [],
            timestamp: ProcessInfo.processInfo.systemUptime,
            windowNumber: windowNumber,
            context: nil,
            characters: "\u{1B}",
            charactersIgnoringModifiers: "\u{1B}",
            isARepeat: false,
            keyCode: 53
        )
        if let syntheticEscape, let keyHandler {
            let isEditingText = firstResponder is NSTextView
            _ = keyHandler(syntheticEscape, isEditingText)
        }
    }

    public func showAnchored(near anchorRect: CGRect?) {
        let fittingSize = contentView?.fittingSize ?? CGSize(width: Self.defaultWidth, height: Self.defaultHeight)
        let panelSize = CGSize(
            width: max(Self.defaultWidth, fittingSize.width),
            height: max(220, min(520, fittingSize.height))
        )

        let targetOrigin = Self.computeOrigin(anchorRect: anchorRect, panelSize: panelSize)
        setFrame(NSRect(origin: targetOrigin, size: panelSize), display: true)

        installKeyMonitor()
        orderFrontRegardless()
        makeKey()
    }

    public func dismiss() {
        removeKeyMonitor()
        orderOut(nil)
    }

    private func installKeyMonitor() {
        removeKeyMonitor()
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            guard let self, self.isVisible, event.window === self else {
                return event
            }
            guard let handler = self.keyHandler else {
                return event
            }
            let isEditingText = self.firstResponder is NSTextView
            return handler(event, isEditingText) ? nil : event
        }
    }

    private func removeKeyMonitor() {
        if let monitor = keyMonitor {
            NSEvent.removeMonitor(monitor)
            keyMonitor = nil
        }
    }

    public static func computeOrigin(anchorRect: CGRect?, panelSize: CGSize) -> CGPoint {
        let anchorPoint: CGPoint
        if let rect = anchorRect, rect.width > 0 || rect.height > 0 || rect.origin != .zero {
            anchorPoint = CGPoint(x: rect.minX, y: rect.minY)
        } else {
            anchorPoint = NSEvent.mouseLocation
        }

        let screen = NSScreen.screens.first(where: { $0.frame.contains(anchorPoint) })
            ?? NSScreen.main
            ?? NSScreen.screens.first
        let visible = (screen?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900))
            .insetBy(dx: 8, dy: 8)

        let gap: CGFloat = 8
        var originX: CGFloat
        var originY: CGFloat

        if let rect = anchorRect, rect.width > 0 || rect.height > 0 || rect.origin != .zero {
            originX = rect.minX
            // Prefer placing directly below the caret/selection; flip above if clipped.
            let belowY = rect.minY - panelSize.height - gap
            if belowY >= visible.minY {
                originY = belowY
            } else {
                originY = rect.maxY + gap
            }
        } else {
            originX = anchorPoint.x + 12
            originY = anchorPoint.y - panelSize.height - 12
        }

        originX = min(max(originX, visible.minX), max(visible.minX, visible.maxX - panelSize.width))
        originY = min(max(originY, visible.minY), max(visible.minY, visible.maxY - panelSize.height))

        return CGPoint(x: originX, y: originY)
    }
}
