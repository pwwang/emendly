import AppKit
import ApplicationServices
import Foundation

public enum AccessibilityCapture {
    public static let contextRadius = 400

    private static let readOnlyRoles: Set<String> = [
        "AXStaticText",
        "AXHeading",
        "AXLink",
        "AXCell",
    ]

    public static func isTrusted(prompt: Bool = false) -> Bool {
        let promptKey = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        let options = [promptKey: prompt] as CFDictionary
        return AXIsProcessTrustedWithOptions(options)
    }

    @MainActor
    public static func primaryScreenHeight() -> CGFloat {
        let primary = NSScreen.screens.first(where: { $0.frame.origin == .zero })
            ?? NSScreen.main
            ?? NSScreen.screens.first
        return primary?.frame.height ?? 0
    }

    public static func capture(
        targetApp: TargetAppInfo,
        primaryScreenHeight: CGFloat
    ) -> CaptureSnapshot? {
        guard isTrusted(prompt: false) else {
            return nil
        }

        guard let focused = focusedElement(for: targetApp.pid) else {
            return nil
        }

        let role = copyStringAttribute(focused, attribute: kAXRoleAttribute as CFString) ?? ""
        let rawValue = copyStringAttribute(focused, attribute: kAXValueAttribute as CFString)
        let selectedRange = copyCFRangeAttribute(focused, attribute: kAXSelectedTextRangeAttribute as CFString)
        var selectedText = copyStringAttribute(focused, attribute: kAXSelectedTextAttribute as CFString)

        if (selectedText == nil || selectedText?.isEmpty == true),
           let value = rawValue,
           let range = selectedRange,
           range.length > 0
        {
            selectedText = sliceUTF16(value, location: range.location, length: range.length)
        }

        let hasAnyTextSignal = selectedText != nil || selectedRange != nil || rawValue != nil
        guard hasAnyTextSignal else {
            return nil
        }

        let isReadOnly = detectReadOnly(element: focused, role: role)
        let finalSelectedText = selectedText ?? ""
        let surrounding = extractSurroundingContext(fieldText: rawValue, selectedRange: selectedRange)
        let anchorRect = extractAnchorRect(
            element: focused,
            selectedRange: selectedRange,
            primaryScreenHeight: primaryScreenHeight
        )

        let verdict: CaptureVerdict
        if isReadOnly {
            verdict = .readOnly
        } else if !finalSelectedText.isEmpty {
            verdict = .selection
        } else {
            verdict = .caret
        }

        return CaptureSnapshot(
            targetApp: targetApp,
            verdict: verdict,
            strategyName: CaptureStrategyKind.ax.rawValue,
            selectedText: finalSelectedText,
            surroundingContext: surrounding,
            anchorRect: anchorRect,
            isReadOnly: isReadOnly
        )
    }

    public static func anchorRect(
        for pid: pid_t,
        primaryScreenHeight: CGFloat
    ) -> CGRect? {
        guard isTrusted(prompt: false),
              let focused = focusedElement(for: pid)
        else {
            return nil
        }
        let selectedRange = copyCFRangeAttribute(focused, attribute: kAXSelectedTextRangeAttribute as CFString)
        return extractAnchorRect(
            element: focused,
            selectedRange: selectedRange,
            primaryScreenHeight: primaryScreenHeight
        )
    }

    public static func replaceSelectedText(_ replacement: String, pid: pid_t) -> Bool {
        guard isTrusted(prompt: false),
              let focused = focusedElement(for: pid)
        else {
            return false
        }

        var elementPid: pid_t = 0
        if AXUIElementGetPid(focused, &elementPid) == .success, elementPid != pid {
            return false
        }

        var settable = DarwinBoolean(false)
        let checkStatus = AXUIElementIsAttributeSettable(
            focused,
            kAXSelectedTextAttribute as CFString,
            &settable
        )
        guard checkStatus == .success, settable.boolValue else {
            return false
        }

        let setStatus = AXUIElementSetAttributeValue(
            focused,
            kAXSelectedTextAttribute as CFString,
            replacement as CFTypeRef
        )
        return setStatus == .success
    }

    public static func axRectToCocoa(_ axRect: CGRect, primaryScreenHeight: CGFloat) -> CGRect {
        guard primaryScreenHeight > 0 else { return axRect }
        let cocoaY = primaryScreenHeight - axRect.origin.y - axRect.size.height
        return CGRect(x: axRect.origin.x, y: cocoaY, width: axRect.size.width, height: axRect.size.height)
    }

    // MARK: - Private AX Helpers

    private static func focusedElement(for pid: pid_t) -> AXUIElement? {
        let appElement = AXUIElementCreateApplication(pid)
        if let appFocused = copyAXElementAttribute(appElement, attribute: kAXFocusedUIElementAttribute as CFString) {
            return appFocused
        }

        let systemWide = AXUIElementCreateSystemWide()
        guard let sysFocused = copyAXElementAttribute(systemWide, attribute: kAXFocusedUIElementAttribute as CFString) else {
            return nil
        }

        var focusedPid: pid_t = 0
        if AXUIElementGetPid(sysFocused, &focusedPid) == .success, focusedPid == pid {
            return sysFocused
        }
        return nil
    }

    private static func detectReadOnly(element: AXUIElement, role: String) -> Bool {
        var valueSettable = DarwinBoolean(false)
        let valueStatus = AXUIElementIsAttributeSettable(element, kAXValueAttribute as CFString, &valueSettable)
        if valueStatus == .success && valueSettable.boolValue {
            return false
        }

        var selectedSettable = DarwinBoolean(false)
        let selectedStatus = AXUIElementIsAttributeSettable(
            element,
            kAXSelectedTextAttribute as CFString,
            &selectedSettable
        )
        if selectedStatus == .success && selectedSettable.boolValue {
            return false
        }

        if readOnlyRoles.contains(role) {
            return true
        }

        if valueStatus == .success || selectedStatus == .success {
            return true
        }

        return false
    }

    private static func extractAnchorRect(
        element: AXUIElement,
        selectedRange: CFRange?,
        primaryScreenHeight: CGFloat
    ) -> CGRect? {
        if var range = selectedRange,
           let rangeValue = AXValueCreate(.cfRange, &range)
        {
            var boundsRef: CFTypeRef?
            let status = AXUIElementCopyParameterizedAttributeValue(
                element,
                kAXBoundsForRangeParameterizedAttribute as CFString,
                rangeValue,
                &boundsRef
            )
            if status == .success,
               let ref = boundsRef,
               CFGetTypeID(ref) == AXValueGetTypeID()
            {
                let axValue = unsafeBitCast(ref, to: AXValue.self)
                var axRect = CGRect.zero
                if AXValueGetValue(axValue, .cgRect, &axRect),
                   axRect.size.height > 0 || axRect.origin != .zero
                {
                    return axRectToCocoa(axRect, primaryScreenHeight: primaryScreenHeight)
                }
            }
        }

        if let position = copyCGPointAttribute(element, attribute: kAXPositionAttribute as CFString),
           let size = copyCGSizeAttribute(element, attribute: kAXSizeAttribute as CFString),
           size.width > 0 && size.height > 0
        {
            let axRect = CGRect(origin: position, size: size)
            return axRectToCocoa(axRect, primaryScreenHeight: primaryScreenHeight)
        }

        return nil
    }

    private static func extractSurroundingContext(
        fieldText: String?,
        selectedRange: CFRange?
    ) -> SurroundingContext {
        guard let fieldText, !fieldText.isEmpty else {
            return .empty
        }

        guard let range = selectedRange else {
            return SurroundingContext(before: "", after: "", fieldText: fieldText)
        }

        let nsText = fieldText as NSString
        let totalLength = nsText.length
        let clampedStart = max(0, min(range.location, totalLength))
        let clampedEnd = max(clampedStart, min(clampedStart + max(0, range.length), totalLength))

        let beforeStart = max(0, clampedStart - contextRadius)
        let beforeLength = clampedStart - beforeStart
        let before = beforeLength > 0
            ? nsText.substring(with: NSRange(location: beforeStart, length: beforeLength))
            : ""

        let afterLength = min(contextRadius, totalLength - clampedEnd)
        let after = afterLength > 0
            ? nsText.substring(with: NSRange(location: clampedEnd, length: afterLength))
            : ""

        return SurroundingContext(before: before, after: after, fieldText: fieldText)
    }

    private static func sliceUTF16(_ string: String, location: Int, length: Int) -> String? {
        let nsString = string as NSString
        let total = nsString.length
        guard location >= 0, length > 0, location < total else {
            return nil
        }
        let clampedLength = min(length, total - location)
        return nsString.substring(with: NSRange(location: location, length: clampedLength))
    }

    private static func copyAXElementAttribute(_ element: AXUIElement, attribute: CFString) -> AXUIElement? {
        var ref: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute, &ref) == .success,
              let value = ref,
              CFGetTypeID(value) == AXUIElementGetTypeID()
        else {
            return nil
        }
        return unsafeBitCast(value, to: AXUIElement.self)
    }

    private static func copyStringAttribute(_ element: AXUIElement, attribute: CFString) -> String? {
        var ref: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute, &ref) == .success else {
            return nil
        }
        return ref as? String
    }

    private static func copyCFRangeAttribute(_ element: AXUIElement, attribute: CFString) -> CFRange? {
        var ref: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute, &ref) == .success,
              let value = ref,
              CFGetTypeID(value) == AXValueGetTypeID()
        else {
            return nil
        }
        let axValue = unsafeBitCast(value, to: AXValue.self)
        var range = CFRange(location: 0, length: 0)
        return AXValueGetValue(axValue, .cfRange, &range) ? range : nil
    }

    private static func copyCGPointAttribute(_ element: AXUIElement, attribute: CFString) -> CGPoint? {
        var ref: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute, &ref) == .success,
              let value = ref,
              CFGetTypeID(value) == AXValueGetTypeID()
        else {
            return nil
        }
        let axValue = unsafeBitCast(value, to: AXValue.self)
        var point = CGPoint.zero
        return AXValueGetValue(axValue, .cgPoint, &point) ? point : nil
    }

    private static func copyCGSizeAttribute(_ element: AXUIElement, attribute: CFString) -> CGSize? {
        var ref: CFTypeRef?
        guard AXUIElementCopyAttributeValue(element, attribute, &ref) == .success,
              let value = ref,
              CFGetTypeID(value) == AXValueGetTypeID()
        else {
            return nil
        }
        let axValue = unsafeBitCast(value, to: AXValue.self)
        var size = CGSize.zero
        return AXValueGetValue(axValue, .cgSize, &size) ? size : nil
    }
}
