import Carbon
import Foundation

public struct HotkeySpec: Sendable, Equatable {
    public static let defaultShortcut = "Cmd+Option+E"

    public let keyCode: UInt32
    public let modifiers: UInt32
    public let displayString: String

    public init(keyCode: UInt32, modifiers: UInt32, displayString: String) {
        self.keyCode = keyCode
        self.modifiers = modifiers
        self.displayString = displayString
    }

    public static func parse(_ raw: String) -> HotkeySpec? {
        let tokens = raw
            .split(separator: "+")
            .map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
            .filter { !$0.isEmpty }

        guard !tokens.isEmpty else {
            return nil
        }

        var modifiers: UInt32 = 0
        var keyCode: UInt32?
        var keyLabel: String?
        var isFunctionKey = false

        for token in tokens {
            let lower = token.lowercased()
            switch lower {
            case "cmd", "command", "⌘", "meta", "super":
                modifiers |= UInt32(cmdKey)
            case "opt", "option", "alt", "⌥":
                modifiers |= UInt32(optionKey)
            case "ctrl", "control", "⌃":
                modifiers |= UInt32(controlKey)
            case "shift", "⇧":
                modifiers |= UInt32(shiftKey)
            default:
                guard keyCode == nil, let resolved = resolveKey(lower) else {
                    return nil
                }
                keyCode = resolved.code
                keyLabel = resolved.label
                isFunctionKey = resolved.isFunctionKey
            }
        }

        guard let finalCode = keyCode, let finalLabel = keyLabel else {
            return nil
        }

        if modifiers == 0 && !isFunctionKey {
            return nil
        }

        var parts: [String] = []
        if (modifiers & UInt32(controlKey)) != 0 { parts.append("Ctrl") }
        if (modifiers & UInt32(cmdKey)) != 0 { parts.append("Cmd") }
        if (modifiers & UInt32(optionKey)) != 0 { parts.append("Option") }
        if (modifiers & UInt32(shiftKey)) != 0 { parts.append("Shift") }
        parts.append(finalLabel)

        return HotkeySpec(
            keyCode: finalCode,
            modifiers: modifiers,
            displayString: parts.joined(separator: "+")
        )
    }

    private static func resolveKey(_ lower: String) -> (code: UInt32, label: String, isFunctionKey: Bool)? {
        let letters: [String: (Int, String)] = [
            "a": (kVK_ANSI_A, "A"), "b": (kVK_ANSI_B, "B"), "c": (kVK_ANSI_C, "C"),
            "d": (kVK_ANSI_D, "D"), "e": (kVK_ANSI_E, "E"), "f": (kVK_ANSI_F, "F"),
            "g": (kVK_ANSI_G, "G"), "h": (kVK_ANSI_H, "H"), "i": (kVK_ANSI_I, "I"),
            "j": (kVK_ANSI_J, "J"), "k": (kVK_ANSI_K, "K"), "l": (kVK_ANSI_L, "L"),
            "m": (kVK_ANSI_M, "M"), "n": (kVK_ANSI_N, "N"), "o": (kVK_ANSI_O, "O"),
            "p": (kVK_ANSI_P, "P"), "q": (kVK_ANSI_Q, "Q"), "r": (kVK_ANSI_R, "R"),
            "s": (kVK_ANSI_S, "S"), "t": (kVK_ANSI_T, "T"), "u": (kVK_ANSI_U, "U"),
            "v": (kVK_ANSI_V, "V"), "w": (kVK_ANSI_W, "W"), "x": (kVK_ANSI_X, "X"),
            "y": (kVK_ANSI_Y, "Y"), "z": (kVK_ANSI_Z, "Z"),
            "0": (kVK_ANSI_0, "0"), "1": (kVK_ANSI_1, "1"), "2": (kVK_ANSI_2, "2"),
            "3": (kVK_ANSI_3, "3"), "4": (kVK_ANSI_4, "4"), "5": (kVK_ANSI_5, "5"),
            "6": (kVK_ANSI_6, "6"), "7": (kVK_ANSI_7, "7"), "8": (kVK_ANSI_8, "8"),
            "9": (kVK_ANSI_9, "9"),
            "space": (kVK_Space, "Space"),
            "return": (kVK_Return, "Return"),
            "enter": (kVK_Return, "Return"),
            "tab": (kVK_Tab, "Tab"),
            "escape": (kVK_Escape, "Esc"),
            "esc": (kVK_Escape, "Esc"),
            "minus": (kVK_ANSI_Minus, "-"), "-": (kVK_ANSI_Minus, "-"),
            "equal": (kVK_ANSI_Equal, "="), "=": (kVK_ANSI_Equal, "="),
            "leftbracket": (kVK_ANSI_LeftBracket, "["), "[": (kVK_ANSI_LeftBracket, "["),
            "rightbracket": (kVK_ANSI_RightBracket, "]"), "]": (kVK_ANSI_RightBracket, "]"),
            "semicolon": (kVK_ANSI_Semicolon, ";"), ";": (kVK_ANSI_Semicolon, ";"),
            "quote": (kVK_ANSI_Quote, "'"), "'": (kVK_ANSI_Quote, "'"),
            "comma": (kVK_ANSI_Comma, ","), ",": (kVK_ANSI_Comma, ","),
            "period": (kVK_ANSI_Period, "."), ".": (kVK_ANSI_Period, "."),
            "slash": (kVK_ANSI_Slash, "/"), "/": (kVK_ANSI_Slash, "/"),
            "backslash": (kVK_ANSI_Backslash, "\\"), "\\": (kVK_ANSI_Backslash, "\\"),
            "grave": (kVK_ANSI_Grave, "`"), "`": (kVK_ANSI_Grave, "`"),
        ]

        if let entry = letters[lower] {
            return (UInt32(entry.0), entry.1, false)
        }

        let functionKeys: [String: (Int, String)] = [
            "f1": (kVK_F1, "F1"), "f2": (kVK_F2, "F2"), "f3": (kVK_F3, "F3"),
            "f4": (kVK_F4, "F4"), "f5": (kVK_F5, "F5"), "f6": (kVK_F6, "F6"),
            "f7": (kVK_F7, "F7"), "f8": (kVK_F8, "F8"), "f9": (kVK_F9, "F9"),
            "f10": (kVK_F10, "F10"), "f11": (kVK_F11, "F11"), "f12": (kVK_F12, "F12"),
        ]

        if let entry = functionKeys[lower] {
            return (UInt32(entry.0), entry.1, true)
        }

        return nil
    }
}

public enum GlobalHotkeyError: LocalizedError, Sendable {
    case invalidShortcut(String)
    case handlerInstallFailed(OSStatus)
    case registrationFailed(OSStatus, String)

    public var errorDescription: String? {
        switch self {
        case .invalidShortcut(let shortcut):
            return "Invalid shortcut: \"\(shortcut)\". Use a modifier plus a key (for example Cmd+Option+E) or a function key (F1–F12)."
        case .handlerInstallFailed(let status):
            return "Failed to install Carbon hotkey event handler (OSStatus \(status))."
        case .registrationFailed(let status, let shortcut):
            return "Failed to register global hotkey \(shortcut) (OSStatus \(status)). It may already be in use."
        }
    }
}

private let emendlyCarbonHotKeyCallback: EventHandlerUPP = { _, eventRef, userData in
    guard let userData, let eventRef else {
        return OSStatus(eventNotHandledErr)
    }

    var hotKeyID = EventHotKeyID(signature: 0, id: 0)
    let status = GetEventParameter(
        eventRef,
        EventParamName(kEventParamDirectObject),
        EventParamType(typeEventHotKeyID),
        nil,
        MemoryLayout<EventHotKeyID>.size,
        nil,
        &hotKeyID
    )

    guard status == noErr else {
        return status
    }

    let instance = Unmanaged<GlobalHotkey>.fromOpaque(userData).takeUnretainedValue()
    instance.handleCarbonEvent(hotKeyID)
    return noErr
}

public final class GlobalHotkey {
    // 4-char signature 'EMND'
    private static let signature: OSType = 0x454D4E44

    private var eventHandlerRef: EventHandlerRef?
    private var hotKeyRef: EventHotKeyRef?
    private var nextID: UInt32 = 1
    private var activeID: UInt32 = 0

    public private(set) var registeredSpec: HotkeySpec?
    public var onTriggered: (@MainActor @Sendable () -> Void)?

    public init() {}

    deinit {
        unregister()
        if let handler = eventHandlerRef {
            RemoveEventHandler(handler)
            eventHandlerRef = nil
        }
    }

    public func register(_ shortcut: String) throws {
        guard let spec = HotkeySpec.parse(shortcut) else {
            throw GlobalHotkeyError.invalidShortcut(shortcut)
        }
        try register(spec)
    }

    public func register(_ spec: HotkeySpec) throws {
        try installEventHandlerIfNeeded()
        unregister()

        let id = nextID
        nextID &+= 1
        if nextID == 0 { nextID = 1 }

        let hotKeyID = EventHotKeyID(signature: Self.signature, id: id)
        var ref: EventHotKeyRef?
        let status = RegisterEventHotKey(
            spec.keyCode,
            spec.modifiers,
            hotKeyID,
            GetApplicationEventTarget(),
            0,
            &ref
        )

        guard status == noErr, let validRef = ref else {
            throw GlobalHotkeyError.registrationFailed(status, spec.displayString)
        }

        hotKeyRef = validRef
        activeID = id
        registeredSpec = spec
    }

    public func unregister() {
        if let ref = hotKeyRef {
            UnregisterEventHotKey(ref)
            hotKeyRef = nil
        }
        activeID = 0
        registeredSpec = nil
    }

    fileprivate func handleCarbonEvent(_ hotKeyID: EventHotKeyID) {
        guard hotKeyID.signature == Self.signature, hotKeyID.id == activeID else {
            return
        }

        guard let callback = onTriggered else {
            return
        }

        Task { @MainActor in
            callback()
        }
    }

    private func installEventHandlerIfNeeded() throws {
        guard eventHandlerRef == nil else {
            return
        }

        var eventType = EventTypeSpec(
            eventClass: OSType(kEventClassKeyboard),
            eventKind: UInt32(kEventHotKeyPressed)
        )

        var ref: EventHandlerRef?
        let status = InstallEventHandler(
            GetApplicationEventTarget(),
            emendlyCarbonHotKeyCallback,
            1,
            &eventType,
            Unmanaged.passUnretained(self).toOpaque(),
            &ref
        )

        guard status == noErr, let validRef = ref else {
            throw GlobalHotkeyError.handlerInstallFailed(status)
        }

        eventHandlerRef = validRef
    }
}
