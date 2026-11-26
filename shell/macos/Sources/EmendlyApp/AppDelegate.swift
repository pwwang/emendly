import AppKit
import EmendlyCapture
import Foundation

@MainActor
public final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private var statusItem: NSStatusItem?
    private var undoMenuItem: NSMenuItem?

    private var settings: AppSettings = AppSettings.load()
    private var captureRules: AppCaptureRules = AppCaptureRules.loadDefault()
    private let clipboardCapture = ClipboardCapture()
    private let globalHotkey = GlobalHotkey()
    private lazy var engineClient = EngineClient(
        baseUrl: settings.baseUrl,
        model: settings.model,
        defaultTargetLanguage: settings.language.defaultTarget
    )

    private var activePanel: FloatingPanel?
    private var activePanelViewModel: PanelViewModel?
    private var activeTargetApp: TargetAppInfo?
    private var settingsController: SettingsWindowController?
    private var hasPromptedAccessibility = false
    private var isCaptureInFlight = false

    public func applicationDidFinishLaunching(_ notification: Notification) {
        setupStatusItem()
        setupHotkey()
        warmUpEngine()
        maybeCheckUpdatesOnStartup()
    }

    public func applicationWillTerminate(_ notification: Notification) {
        globalHotkey.unregister()
        let client = engineClient
        Task {
            await client.shutdown()
        }
    }

    // MARK: - Menu Bar

    private func setupStatusItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        if let button = item.button {
            if let image = NSImage(systemSymbolName: "wand.and.stars", accessibilityDescription: "Emendly") {
                image.isTemplate = true
                button.image = image
            } else {
                button.title = "E"
            }
            button.toolTip = "Emendly — \(settings.hotkey)"
        }

        let menu = NSMenu()
        menu.delegate = self

        let versionItem = NSMenuItem(
            title: "Emendly \(VersionInfo.shared.appVersion)",
            action: nil,
            keyEquivalent: ""
        )
        versionItem.isEnabled = false
        menu.addItem(versionItem)
        menu.addItem(.separator())

        let settingsItem = NSMenuItem(
            title: "Settings…",
            action: #selector(openSettingsFromMenu),
            keyEquivalent: ","
        )
        settingsItem.target = self
        menu.addItem(settingsItem)

        let undoItem = NSMenuItem(
            title: "Undo last insert",
            action: #selector(undoLastInsertFromMenu),
            keyEquivalent: ""
        )
        undoItem.target = self
        undoItem.isEnabled = false
        menu.addItem(undoItem)
        self.undoMenuItem = undoItem

        let updateItem = NSMenuItem(
            title: "Check for updates…",
            action: #selector(checkForUpdatesFromMenu),
            keyEquivalent: ""
        )
        updateItem.target = self
        menu.addItem(updateItem)

        menu.addItem(.separator())

        let quitItem = NSMenuItem(
            title: "Quit",
            action: #selector(quitApplication),
            keyEquivalent: "q"
        )
        quitItem.target = self
        menu.addItem(quitItem)

        item.menu = menu
        self.statusItem = item
    }

    public func menuNeedsUpdate(_ menu: NSMenu) {
        undoMenuItem?.isEnabled = clipboardCapture.hasUndo
    }

    @objc private func openSettingsFromMenu() {
        openSettings(besidePanelFrame: nil)
    }

    @objc private func undoLastInsertFromMenu() {
        Task {
            _ = await clipboardCapture.undoLastInsert()
        }
    }

    @objc private func checkForUpdatesFromMenu() {
        let currentVersion = VersionInfo.shared.appVersion
        Task { [weak self] in
            let result = await UpdateChecker.check(currentVersion: currentVersion)
            guard let self else { return }
            self.settings.lastUpdateCheckUtc = ISO8601DateFormatter().string(from: Date())
            try? self.settings.save()
            self.presentUpdateAlert(result)
        }
    }

    @objc private func quitApplication() {
        NSApp.terminate(nil)
    }

    // MARK: - Hotkey & Activation

    private func setupHotkey() {
        globalHotkey.onTriggered = { [weak self] in
            self?.handleHotkeyPressed()
        }

        do {
            try globalHotkey.register(settings.hotkey)
        } catch {
            ShellLogger.logMeta(event: "hotkey", app: "emendly", status: "register_failed")
            if settings.hotkey != AppSettings.defaultHotkey {
                try? globalHotkey.register(AppSettings.defaultHotkey)
            }
        }
    }

    private func handleHotkeyPressed() {
        if let panel = activePanel, panel.isVisible {
            activePanelViewModel?.closePanel(restoreFocus: true)
            return
        }

        guard !isCaptureInFlight else {
            return
        }

        if !AccessibilityCapture.isTrusted(prompt: false) && !hasPromptedAccessibility {
            hasPromptedAccessibility = true
            _ = AccessibilityCapture.isTrusted(prompt: true)
            ShellLogger.logMeta(event: "accessibility", app: "emendly", status: "prompted")
        }

        guard let targetApp = CaptureChain.frontmostTargetApp() else {
            return
        }

        isCaptureInFlight = true
        let rules = captureRules
        let screenHeight = AccessibilityCapture.primaryScreenHeight()

        Task.detached(priority: .userInitiated) { [weak self] in
            let snapshot = CaptureChain.capture(
                targetApp: targetApp,
                rules: rules,
                primaryScreenHeight: screenHeight
            )
            await MainActor.run {
                guard let self else { return }
                self.isCaptureInFlight = false
                ShellLogger.logProbe(
                    app: snapshot.targetApp.localizedName,
                    verdict: snapshot.verdict.rawValue,
                    strategy: snapshot.strategyName
                )
                self.showPanel(for: snapshot)
            }
        }
    }

    private func showPanel(for snapshot: CaptureSnapshot) {
        dismissActivePanel(restoreFocus: false)
        activeTargetApp = snapshot.targetApp

        let vm = PanelViewModel(
            snapshot: snapshot,
            settings: settings,
            engineClient: engineClient,
            clipboardCapture: clipboardCapture,
            onSaveSettings: { [weak self] updated in
                guard let self else { return }
                self.settings = updated
                try? updated.save()
                Task {
                    try? await self.engineClient.configure(
                        baseUrl: updated.baseUrl,
                        model: updated.model,
                        defaultTargetLanguage: updated.language.defaultTarget
                    )
                }
            },
            onOpenSettings: { [weak self] in
                guard let self else { return }
                self.openSettings(besidePanelFrame: self.activePanel?.frame)
            },
            onClose: { [weak self] restoreFocus in
                self?.dismissActivePanel(restoreFocus: restoreFocus)
            }
        )

        let panel = FloatingPanel(
            contentView: PanelView(viewModel: vm),
            onKeyEvent: { [weak vm] event, isEditing in
                vm?.handleKeyEvent(event, isEditingText: isEditing) ?? false
            }
        )

        activePanelViewModel = vm
        activePanel = panel
        panel.showAnchored(near: snapshot.anchorRect)
    }

    private func dismissActivePanel(restoreFocus: Bool) {
        let targetToRestore = activeTargetApp
        activePanel?.dismiss()
        activePanel = nil
        activePanelViewModel = nil
        activeTargetApp = nil

        if restoreFocus,
           let target = targetToRestore,
           let app = NSRunningApplication(processIdentifier: target.pid),
           !app.isTerminated
        {
            ClipboardCapture.activateApplication(app)
        }
    }

    // MARK: - Settings & Engine

    private func openSettings(besidePanelFrame panelFrame: CGRect?) {
        let vm = SettingsViewModel(
            settings: settings,
            onApply: { [weak self] updated in
                guard let self else { return }
                if updated.hotkey != self.settings.hotkey || self.globalHotkey.registeredSpec == nil {
                    try self.globalHotkey.register(updated.hotkey)
                }
                try updated.save()
                self.settings = updated
                self.statusItem?.button?.toolTip = "Emendly — \(updated.hotkey)"

                let client = self.engineClient
                Task {
                    try? await client.configure(
                        baseUrl: updated.baseUrl,
                        model: updated.model,
                        defaultTargetLanguage: updated.language.defaultTarget
                    )
                }
            },
            onClose: { [weak self] in
                self?.settingsController?.close()
                self?.settingsController = nil
            }
        )

        let controller = SettingsWindowController(viewModel: vm, besidePanelFrame: panelFrame)
        settingsController = controller
        controller.present(besidePanelFrame: panelFrame)
    }

    private func warmUpEngine() {
        let current = settings
        let client = engineClient
        Task {
            do {
                try await client.warmUp(
                    baseUrl: current.baseUrl,
                    model: current.model,
                    defaultTargetLanguage: current.language.defaultTarget
                )
                ShellLogger.logMeta(event: "startup", app: "emendly", status: "engine_ready")
            } catch {
                ShellLogger.logMeta(event: "startup", app: "emendly", status: "engine_unavailable")
            }
        }
    }

    // MARK: - Updates

    private func maybeCheckUpdatesOnStartup() {
        guard settings.checkUpdatesOnStartup else { return }

        if let lastRaw = settings.lastUpdateCheckUtc,
           let lastDate = ISO8601DateFormatter().date(from: lastRaw),
           Date().timeIntervalSince(lastDate) < 86_400
        {
            return
        }

        let currentVersion = VersionInfo.shared.appVersion
        Task { [weak self] in
            let result = await UpdateChecker.check(currentVersion: currentVersion)
            guard let self else { return }
            self.settings.lastUpdateCheckUtc = ISO8601DateFormatter().string(from: Date())
            try? self.settings.save()
            if case .updateAvailable = result {
                self.presentUpdateAlert(result)
            }
        }
    }

    private func presentUpdateAlert(_ result: UpdateCheckResult) {
        let alert = NSAlert()
        switch result {
        case .upToDate(let version):
            alert.messageText = "Emendly is up to date"
            alert.informativeText = "You are running the latest version (v\(version))."
            alert.addButton(withTitle: "OK")
            NSApp.activate(ignoringOtherApps: true)
            alert.runModal()

        case .updateAvailable(let latest, let downloadURL, _):
            alert.messageText = "Update available: Emendly v\(latest)"
            alert.informativeText = "A newer release of Emendly for macOS is available to download."
            alert.addButton(withTitle: "Download")
            alert.addButton(withTitle: "Later")
            NSApp.activate(ignoringOtherApps: true)
            if alert.runModal() == .alertFirstButtonReturn {
                UpdateChecker.open(downloadURL)
            }

        case .failed(let message):
            alert.messageText = "Could not check for updates"
            alert.informativeText = message
            alert.addButton(withTitle: "OK")
            NSApp.activate(ignoringOtherApps: true)
            alert.runModal()
        }
    }
}
