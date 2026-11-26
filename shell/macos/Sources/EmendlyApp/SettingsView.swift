import AppKit
import Combine
import EmendlyCapture
import SwiftUI

@MainActor
public final class SettingsViewModel: ObservableObject {
    @Published public var hotkey: String
    @Published public var defaultLanguage: String
    @Published public var baseUrl: String
    @Published public var model: String
    @Published public var checkUpdatesOnStartup: Bool

    @Published public var apiKeyInput: String = ""
    @Published public var apiKeySource: ApiKeySource = .none
    @Published public var isAccessibilityTrusted: Bool = false

    @Published public var isCheckingUpdates: Bool = false
    @Published public var updateStatusText: String?
    @Published public var updateDownloadURL: URL?

    @Published public var feedbackMessage: String?
    @Published public var isFeedbackError: Bool = false

    private var currentSettings: AppSettings
    private let onApply: (AppSettings) throws -> Void
    private let onClose: () -> Void

    public init(
        settings: AppSettings,
        onApply: @escaping (AppSettings) throws -> Void,
        onClose: @escaping () -> Void
    ) {
        self.currentSettings = settings
        self.hotkey = settings.hotkey
        self.defaultLanguage = settings.language.defaultTarget
        self.baseUrl = settings.baseUrl
        self.model = settings.model
        self.checkUpdatesOnStartup = settings.checkUpdatesOnStartup
        self.onApply = onApply
        self.onClose = onClose
        refreshStatuses()
    }

    public func refreshStatuses() {
        apiKeySource = KeychainHelper.resolveApiKey().source
        isAccessibilityTrusted = AccessibilityCapture.isTrusted(prompt: false)
    }

    public var apiKeyStatusLabel: String {
        switch apiKeySource {
        case .environment:
            return "Detected in EMENDLY_API_KEY environment variable"
        case .keychain:
            return "Saved in macOS Keychain (never stored on disk)"
        case .none:
            return "Not set — save to Keychain or set EMENDLY_API_KEY"
        }
    }

    public func saveApiKeyToKeychain() {
        let trimmed = apiKeyInput.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            setFeedback("Enter an API key before saving.", isError: true)
            return
        }
        do {
            try KeychainHelper.saveApiKey(trimmed)
            apiKeyInput = ""
            refreshStatuses()
            setFeedback("API key saved to macOS Keychain.", isError: false)
            try? onApply(currentSettings)
        } catch {
            setFeedback(error.localizedDescription, isError: true)
        }
    }

    public func clearKeychainApiKey() {
        do {
            try KeychainHelper.deleteApiKey()
            refreshStatuses()
            setFeedback("Removed API key from macOS Keychain.", isError: false)
            try? onApply(currentSettings)
        } catch {
            setFeedback(error.localizedDescription, isError: true)
        }
    }

    public func openAccessibilityPreferences() {
        _ = AccessibilityCapture.isTrusted(prompt: true)
        if let url = URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") {
            _ = NSWorkspace.shared.open(url)
        }
    }

    public func checkForUpdates() {
        guard !isCheckingUpdates else { return }
        isCheckingUpdates = true
        updateStatusText = "Checking GitHub releases…"
        updateDownloadURL = nil

        let currentVersion = VersionInfo.shared.appVersion
        Task { [weak self] in
            let result = await UpdateChecker.check(currentVersion: currentVersion)
            guard let self else { return }
            self.isCheckingUpdates = false
            self.currentSettings.lastUpdateCheckUtc = ISO8601DateFormatter().string(from: Date())
            try? self.currentSettings.save()

            switch result {
            case .upToDate(let version):
                self.updateStatusText = "You are up to date (v\(version))."
            case .updateAvailable(let latest, let downloadURL, _):
                self.updateStatusText = "Update available: v\(latest)"
                self.updateDownloadURL = downloadURL
            case .failed(let message):
                self.updateStatusText = message
            }
        }
    }

    public func openUpdateDownload() {
        if let url = updateDownloadURL {
            UpdateChecker.open(url)
        }
    }

    public func saveSettings() {
        let trimmedHotkey = hotkey.trimmingCharacters(in: .whitespacesAndNewlines)
        guard let parsedHotkey = HotkeySpec.parse(trimmedHotkey) else {
            setFeedback("Invalid hotkey. Try Cmd+Option+E, Ctrl+Alt+E, or F6.", isError: true)
            return
        }

        let trimmedBase = baseUrl.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedBase.isEmpty else {
            setFeedback("Base URL must not be empty.", isError: true)
            return
        }

        let trimmedModel = model.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmedModel.isEmpty else {
            setFeedback("Model must not be empty.", isError: true)
            return
        }

        let trimmedLang = defaultLanguage.trimmingCharacters(in: .whitespacesAndNewlines)
        let resolvedLang = trimmedLang.isEmpty ? LanguageSettings.defaultTargetLanguage : trimmedLang

        var updated = currentSettings
        updated.hotkey = parsedHotkey.displayString
        updated.baseUrl = trimmedBase
        updated.model = trimmedModel
        updated.language.defaultTarget = resolvedLang
        updated.checkUpdatesOnStartup = checkUpdatesOnStartup

        do {
            try onApply(updated)
            currentSettings = updated
            hotkey = updated.hotkey
            defaultLanguage = updated.language.defaultTarget
            setFeedback("Settings saved.", isError: false)
        } catch {
            setFeedback(error.localizedDescription, isError: true)
        }
    }

    public func close() {
        onClose()
    }

    private func setFeedback(_ text: String, isError: Bool) {
        feedbackMessage = text
        isFeedbackError = isError
    }
}

public struct SettingsView: View {
    @ObservedObject public var viewModel: SettingsViewModel
    @ObservedObject private var versionInfo = VersionInfo.shared

    public init(viewModel: SettingsViewModel) {
        self.viewModel = viewModel
    }

    public var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Emendly Settings")
                .font(.system(size: 16, weight: .semibold))
                .foregroundColor(PanelPalette.primaryText)

            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    sectionCard(title: "Shortcut & Language") {
                        labeledField("Global Hotkey") {
                            TextField("Cmd+Option+E", text: $viewModel.hotkey)
                                .textFieldStyle(.roundedBorder)
                        }

                        labeledField("Default Translation Target") {
                            HStack(spacing: 8) {
                                TextField("German", text: $viewModel.defaultLanguage)
                                    .textFieldStyle(.roundedBorder)

                                Menu("Presets") {
                                    ForEach(MenuModel.defaultLanguages, id: \.self) { lang in
                                        Button(lang) {
                                            viewModel.defaultLanguage = lang
                                        }
                                    }
                                }
                                .fixedSize()
                            }
                        }
                    }

                    sectionCard(title: "Provider") {
                        labeledField("Base URL") {
                            TextField("https://api.openai.com/v1", text: $viewModel.baseUrl)
                                .textFieldStyle(.roundedBorder)
                        }

                        labeledField("Model") {
                            TextField("gpt-5-mini", text: $viewModel.model)
                                .textFieldStyle(.roundedBorder)
                        }

                        VStack(alignment: .leading, spacing: 6) {
                            Text("API Key")
                                .font(.system(size: 11, weight: .medium))
                                .foregroundColor(PanelPalette.secondaryText)

                            Text(viewModel.apiKeyStatusLabel)
                                .font(.system(size: 11))
                                .foregroundColor(
                                    viewModel.apiKeySource == .none ? PanelPalette.danger : Color.green.opacity(0.9)
                                )

                            HStack(spacing: 8) {
                                SecureField("sk-…", text: $viewModel.apiKeyInput)
                                    .textFieldStyle(.roundedBorder)
                                Button("Save to Keychain") {
                                    viewModel.saveApiKeyToKeychain()
                                }
                                .buttonStyle(.bordered)

                                Button("Clear") {
                                    viewModel.clearKeychainApiKey()
                                }
                                .buttonStyle(.bordered)
                            }
                        }
                    }

                    sectionCard(title: "Accessibility Permission") {
                        HStack {
                            Circle()
                                .fill(viewModel.isAccessibilityTrusted ? Color.green : Color.orange)
                                .frame(width: 8, height: 8)
                            Text(viewModel.isAccessibilityTrusted ? "Granted" : "Not granted")
                                .font(.system(size: 12, weight: .medium))
                                .foregroundColor(PanelPalette.primaryText)

                            Spacer()

                            Button("Refresh") {
                                viewModel.refreshStatuses()
                            }
                            .buttonStyle(.bordered)

                            Button("Open Accessibility Settings") {
                                viewModel.openAccessibilityPreferences()
                            }
                            .buttonStyle(.borderedProminent)
                            .tint(PanelPalette.accent)
                        }
                    }

                    sectionCard(title: "Updates") {
                        HStack {
                            Toggle("Check for updates on startup", isOn: $viewModel.checkUpdatesOnStartup)
                                .toggleStyle(.checkbox)
                                .font(.system(size: 12))
                                .foregroundColor(PanelPalette.primaryText)

                            Spacer()

                            Button(viewModel.isCheckingUpdates ? "Checking…" : "Check now") {
                                viewModel.checkForUpdates()
                            }
                            .disabled(viewModel.isCheckingUpdates)
                            .buttonStyle(.bordered)
                        }

                        if let status = viewModel.updateStatusText {
                            HStack(spacing: 8) {
                                Text(status)
                                    .font(.system(size: 11))
                                    .foregroundColor(PanelPalette.secondaryText)
                                if viewModel.updateDownloadURL != nil {
                                    Button("Download") {
                                        viewModel.openUpdateDownload()
                                    }
                                    .buttonStyle(.link)
                                }
                            }
                        }
                    }
                }
            }

            if let message = viewModel.feedbackMessage {
                Text(message)
                    .font(.system(size: 11, weight: .medium))
                    .foregroundColor(viewModel.isFeedbackError ? PanelPalette.danger : Color.green.opacity(0.9))
            }

            Divider().background(PanelPalette.border)

            HStack {
                Text(versionInfo.formattedFooter)
                    .font(.system(size: 10, design: .monospaced))
                    .foregroundColor(PanelPalette.secondaryText)

                Spacer()

                Button("Close") {
                    viewModel.close()
                }
                .buttonStyle(.bordered)

                Button("Save") {
                    viewModel.saveSettings()
                }
                .buttonStyle(.borderedProminent)
                .tint(PanelPalette.accent)
            }
        }
        .padding(16)
        .frame(width: 460, height: 560)
        .background(PanelPalette.background)
        .preferredColorScheme(.dark)
        .onAppear {
            viewModel.refreshStatuses()
        }
    }

    private func sectionCard<Content: View>(title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title.uppercased())
                .font(.system(size: 10, weight: .semibold))
                .foregroundColor(PanelPalette.secondaryText)
            VStack(alignment: .leading, spacing: 10) {
                content()
            }
            .padding(10)
            .frame(maxWidth: .infinity, alignment: .leading)
            .background(PanelPalette.surface)
            .clipShape(RoundedRectangle(cornerRadius: 8, style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: 8, style: .continuous)
                    .stroke(PanelPalette.border, lineWidth: 1)
            )
        }
    }

    private func labeledField<Content: View>(_ label: String, @ViewBuilder field: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(label)
                .font(.system(size: 11, weight: .medium))
                .foregroundColor(PanelPalette.secondaryText)
            field()
        }
    }
}

@MainActor
public final class SettingsWindowController: NSWindowController {
    private let viewModel: SettingsViewModel

    public init(viewModel: SettingsViewModel, besidePanelFrame panelFrame: CGRect? = nil) {
        self.viewModel = viewModel

        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 460, height: 560),
            styleMask: [.titled, .closable, .fullSizeContentView],
            backing: .buffered,
            defer: false
        )
        window.title = "Emendly Settings"
        window.titlebarAppearsTransparent = true
        window.appearance = NSAppearance(named: .darkAqua)
        window.backgroundColor = NSColor(red: 0x14 / 255.0, green: 0x14 / 255.0, blue: 0x19 / 255.0, alpha: 1.0)
        window.isReleasedWhenClosed = false
        window.contentView = NSHostingView(rootView: SettingsView(viewModel: viewModel))

        super.init(window: window)

        if let panelFrame {
            positionBeside(panelFrame: panelFrame)
        } else {
            window.center()
        }
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) {
        fatalError("init(coder:) has not been implemented")
    }

    public func present(besidePanelFrame panelFrame: CGRect? = nil) {
        viewModel.refreshStatuses()
        if let panelFrame {
            positionBeside(panelFrame: panelFrame)
        }
        window?.level = panelFrame != nil ? .floating : .normal
        showWindow(nil)
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }

    private func positionBeside(panelFrame: CGRect) {
        guard let window else { return }
        let size = window.frame.size
        let gap: CGFloat = 12

        let screen = NSScreen.screens.first(where: { $0.frame.intersects(panelFrame) })
            ?? NSScreen.main
            ?? NSScreen.screens.first
        let visible = (screen?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1440, height: 900))
            .insetBy(dx: 8, dy: 8)

        var x = panelFrame.maxX + gap
        if x + size.width > visible.maxX {
            x = panelFrame.minX - size.width - gap
        }
        x = min(max(x, visible.minX), max(visible.minX, visible.maxX - size.width))

        let y = min(
            max(panelFrame.maxY - size.height, visible.minY),
            max(visible.minY, visible.maxY - size.height)
        )

        window.setFrameOrigin(NSPoint(x: x, y: y))
    }
}
