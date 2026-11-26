import AppKit
import Combine
import EmendlyCapture
import SwiftUI

public enum PanelPalette {
    public static let background = Color(red: 0x14 / 255.0, green: 0x14 / 255.0, blue: 0x19 / 255.0)
    public static let surface = Color(red: 0x1E / 255.0, green: 0x1E / 255.0, blue: 0x26 / 255.0)
    public static let surfaceHover = Color(red: 0x27 / 255.0, green: 0x27 / 255.0, blue: 0x33 / 255.0)
    public static let accent = Color(red: 0x25 / 255.0, green: 0x63 / 255.0, blue: 0xEB / 255.0)
    public static let border = Color.white.opacity(0.10)
    public static let primaryText = Color.white.opacity(0.94)
    public static let secondaryText = Color.white.opacity(0.60)
    public static let danger = Color(red: 0xEF / 255.0, green: 0x44 / 255.0, blue: 0x44 / 255.0)
}

public enum PanelStage: Equatable {
    case chooseAction
    case promptInput(MenuAction)
    case streaming(MenuAction)
    case previewReady(MenuAction)
    case error(MenuAction, message: String, retryable: Bool)
}

@MainActor
public final class PanelViewModel: ObservableObject {
    @Published public var stage: PanelStage = .chooseAction
    @Published public var isExpanded: Bool = false
    @Published public var filterText: String = "" {
        didSet {
            selectedIndex = 0
        }
    }
    @Published public var selectedIndex: Int = 0
    @Published public var promptInputText: String = ""
    @Published public var outputText: String = ""
    @Published public var selectedLanguage: String
    @Published public var includeContext: Bool
    @Published public var statusMessage: String?

    public let snapshot: CaptureSnapshot
    public let classification: SelectionClassification
    private let engineClient: EngineClient
    private let clipboardCapture: ClipboardCapture
    private var settings: AppSettings
    private let onSaveSettings: (AppSettings) -> Void
    private let onOpenSettings: () -> Void
    private let onClose: (Bool) -> Void

    private var activeTask: Task<Void, Never>?
    private var lastSubmittedPrompt: String = ""

    public init(
        snapshot: CaptureSnapshot,
        settings: AppSettings,
        engineClient: EngineClient,
        clipboardCapture: ClipboardCapture,
        onSaveSettings: @escaping (AppSettings) -> Void,
        onOpenSettings: @escaping () -> Void,
        onClose: @escaping (Bool) -> Void
    ) {
        self.snapshot = snapshot
        self.classification = snapshot.classification
        self.settings = settings
        self.engineClient = engineClient
        self.clipboardCapture = clipboardCapture
        self.onSaveSettings = onSaveSettings
        self.onOpenSettings = onOpenSettings
        self.onClose = onClose
        self.selectedLanguage = settings.language.defaultTarget
        self.includeContext = snapshot.surroundingContext.hasContext
            && settings.isContextEnabled(for: MenuModel.rewrite.id, defaultValue: true)
    }

    public var visibleActions: [MenuAction] {
        if isExpanded {
            return MenuModel.filterExpanded(prefix: filterText).flatMap(\.items)
        }
        return MenuModel.defaultActions(for: classification.state)
    }

    public var filteredGroups: [MenuGroup] {
        MenuModel.filterExpanded(prefix: filterText)
    }

    public var strategyBadge: String {
        switch snapshot.strategyName {
        case CaptureStrategyKind.ax.rawValue:
            return snapshot.isReadOnly ? "AX · RO" : "AX"
        case CaptureStrategyKind.clipboard.rawValue:
            return "CLIP"
        case CaptureStrategyKind.clipboardReadOnly.rawValue:
            return "CLIP · RO"
        case CaptureStrategyKind.clipboardNoKeys.rawValue:
            return "NO-KEYS"
        default:
            return snapshot.strategyName.uppercased()
        }
    }

    public func isCopyOnly(for action: MenuAction) -> Bool {
        snapshot.isReadOnly || action.kind == .referenceList
    }

    public func primaryButtonTitle(for action: MenuAction) -> String {
        if isCopyOnly(for: action) {
            return "Copy"
        }
        if classification.state == .empty {
            return "Insert"
        }
        return "Replace"
    }

    public func toggleContext(for actionId: String) {
        includeContext.toggle()
        settings.setContextEnabled(includeContext, for: actionId)
        onSaveSettings(settings)
    }

    public func selectLanguage(_ language: String) {
        selectedLanguage = language
        settings.language.defaultTarget = language
        onSaveSettings(settings)
    }

    public func openSettings() {
        onOpenSettings()
    }

    public func closePanel(restoreFocus: Bool = true) {
        cancelActiveRequest()
        onClose(restoreFocus)
    }

    public func cancelActiveRequest() {
        activeTask?.cancel()
        activeTask = nil
        Task {
            await engineClient.cancelActiveRun()
        }
    }

    public func selectAction(_ action: MenuAction) {
        statusMessage = nil
        if action.id == MenuModel.compose.id || action.id == MenuModel.custom.id {
            promptInputText = ""
            includeContext = snapshot.surroundingContext.hasContext
                && settings.isContextEnabled(for: action.id, defaultValue: true)
            stage = .promptInput(action)
            return
        }

        if action.options == .context {
            includeContext = snapshot.surroundingContext.hasContext
                && settings.isContextEnabled(for: action.id, defaultValue: true)
        }

        runAction(action, promptText: nil)
    }

    public func submitPromptInput(for action: MenuAction) {
        let trimmed = promptInputText.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else {
            statusMessage = "Enter a brief or instruction first."
            return
        }
        statusMessage = nil
        lastSubmittedPrompt = trimmed
        runAction(action, promptText: trimmed)
    }

    public func retry(_ action: MenuAction) {
        let prompt = (action.id == MenuModel.compose.id || action.id == MenuModel.custom.id)
            ? lastSubmittedPrompt
            : nil
        runAction(action, promptText: prompt)
    }

    public func goBackToChooseAction() {
        cancelActiveRequest()
        outputText = ""
        statusMessage = nil
        stage = .chooseAction
    }

    public func acceptResult(for action: MenuAction) {
        let text = outputText
        guard !text.isEmpty else {
            closePanel(restoreFocus: true)
            return
        }

        if isCopyOnly(for: action) {
            ClipboardCapture.copyToPasteboard(text)
            closePanel(restoreFocus: true)
            return
        }

        let finalReplacement = classification.restoreSurroundingWhitespace(text)
        let targetSnapshot = snapshot
        let clipboard = clipboardCapture
        let actionId = action.id

        // Close the panel before write-back so focus returns cleanly to the target field.
        closePanel(restoreFocus: false)

        Task {
            let ok = await CaptureChain.writeBack(
                replacement: finalReplacement,
                snapshot: targetSnapshot,
                actionName: actionId,
                clipboard: clipboard
            )
            if !ok {
                ClipboardCapture.copyToPasteboard(finalReplacement)
            }
        }
    }

    public func copyResult() {
        guard !outputText.isEmpty else { return }
        ClipboardCapture.copyToPasteboard(outputText)
        closePanel(restoreFocus: true)
    }

    public func handleKeyEvent(_ event: NSEvent, isEditingText: Bool) -> Bool {
        let keyCode = event.keyCode
        let mods = event.modifierFlags.intersection([.command, .option, .control, .shift])

        // Escape always backs out or closes.
        if keyCode == 53 {
            switch stage {
            case .chooseAction:
                if isExpanded && !filterText.isEmpty {
                    filterText = ""
                } else if isExpanded {
                    isExpanded = false
                    selectedIndex = 0
                } else {
                    closePanel(restoreFocus: true)
                }
            case .promptInput:
                goBackToChooseAction()
            case .streaming:
                goBackToChooseAction()
            case .previewReady, .error:
                goBackToChooseAction()
            }
            return true
        }

        switch stage {
        case .chooseAction:
            if keyCode == 48 && mods.isEmpty { // Tab
                isExpanded.toggle()
                filterText = ""
                selectedIndex = 0
                return true
            }

            if keyCode == 126 { // Up arrow
                let count = visibleActions.count
                if count > 0 {
                    selectedIndex = (selectedIndex - 1 + count) % count
                }
                return true
            }

            if keyCode == 125 { // Down arrow
                let count = visibleActions.count
                if count > 0 {
                    selectedIndex = (selectedIndex + 1) % count
                }
                return true
            }

            if (keyCode == 36 || keyCode == 76) && mods.isEmpty { // Return / Enter
                let actions = visibleActions
                if selectedIndex >= 0 && selectedIndex < actions.count {
                    selectAction(actions[selectedIndex])
                    return true
                }
            }

            if !isEditingText && mods.isEmpty,
               let chars = event.charactersIgnoringModifiers,
               chars.count == 1,
               let digit = Int(chars),
               digit >= 1 && digit <= 9
            {
                let actions = visibleActions
                let idx = digit - 1
                if idx < actions.count {
                    selectAction(actions[idx])
                    return true
                }
            }
            return false

        case .promptInput(let action):
            if (keyCode == 36 || keyCode == 76) && (mods.contains(.command) || (!mods.contains(.shift) && mods.isEmpty)) {
                submitPromptInput(for: action)
                return true
            }
            return false

        case .streaming:
            return false

        case .previewReady(let action):
            if (keyCode == 36 || keyCode == 76) && mods.isEmpty {
                acceptResult(for: action)
                return true
            }
            if mods == .command, event.charactersIgnoringModifiers?.lowercased() == "c" {
                copyResult()
                return true
            }
            return false

        case .error(let action, _, _):
            if (keyCode == 36 || keyCode == 76) && mods.isEmpty {
                retry(action)
                return true
            }
            return false
        }
    }

    private func runAction(_ action: MenuAction, promptText: String?) {
        cancelActiveRequest()
        outputText = ""
        statusMessage = nil
        stage = .streaming(action)

        let inputText = classification.text
        var options = EngineActionOptions()

        if action.id == MenuModel.translate.id {
            options.targetLang = selectedLanguage
        }
        if action.id == MenuModel.compose.id {
            options.brief = promptText
        }
        if action.id == MenuModel.custom.id {
            options.instruction = promptText
        }
        if includeContext && snapshot.surroundingContext.hasContext {
            options.includeContext = true
            if !snapshot.surroundingContext.before.isEmpty {
                options.contextBefore = snapshot.surroundingContext.before
            }
            if !snapshot.surroundingContext.after.isEmpty {
                options.contextAfter = snapshot.surroundingContext.after
            }
        }

        let appName = snapshot.targetApp.localizedName
        let client = engineClient

        activeTask = Task { [weak self] in
            let start = Date()
            do {
                let result = try await client.runAction(
                    action: action.id,
                    text: inputText,
                    options: options,
                    context: nil,
                    app: appName,
                    onDelta: { [weak self] delta in
                        self?.outputText.append(delta)
                    }
                )
                guard let self, !Task.isCancelled else { return }
                self.outputText = result.text
                self.stage = .previewReady(action)
                ShellLogger.logAction(
                    action: action.id,
                    app: appName,
                    latencyMs: result.latencyMs,
                    inputBytes: inputText.utf8.count,
                    outputBytes: result.text.utf8.count,
                    status: "ok"
                )
            } catch is CancellationError {
                return
            } catch let clientError as EngineClientError {
                guard let self, !Task.isCancelled else { return }
                if case .cancelled = clientError {
                    return
                }
                let elapsed = UInt64(max(0, Date().timeIntervalSince(start) * 1000))
                ShellLogger.logAction(
                    action: action.id,
                    app: appName,
                    latencyMs: elapsed,
                    inputBytes: inputText.utf8.count,
                    outputBytes: 0,
                    status: "error"
                )
                self.stage = .error(
                    action,
                    message: clientError.localizedDescription,
                    retryable: clientError.isRetryable
                )
            } catch {
                guard let self, !Task.isCancelled else { return }
                self.stage = .error(
                    action,
                    message: error.localizedDescription,
                    retryable: false
                )
            }
        }
    }
}

public struct PanelView: View {
    @ObservedObject public var viewModel: PanelViewModel

    public init(viewModel: PanelViewModel) {
        self.viewModel = viewModel
    }

    public var body: some View {
        VStack(spacing: 0) {
            headerBar
            Divider().background(PanelPalette.border)
            contentArea
        }
        .frame(width: FloatingPanel.defaultWidth)
        .background(PanelPalette.background)
        .clipShape(RoundedRectangle(cornerRadius: 12, style: .continuous))
        .overlay(
            RoundedRectangle(cornerRadius: 12, style: .continuous)
                .stroke(PanelPalette.border, lineWidth: 1)
        )
        .preferredColorScheme(.dark)
    }

    private var headerBar: some View {
        HStack(spacing: 8) {
            Text("Emendly")
                .font(.system(size: 13, weight: .semibold))
                .foregroundColor(PanelPalette.primaryText)

            Text(viewModel.snapshot.targetApp.localizedName)
                .font(.system(size: 11, weight: .medium))
                .foregroundColor(PanelPalette.secondaryText)
                .lineLimit(1)
                .truncationMode(.tail)

            Text(viewModel.strategyBadge)
                .font(.system(size: 9, weight: .semibold, design: .monospaced))
                .foregroundColor(PanelPalette.secondaryText)
                .padding(.horizontal, 5)
                .padding(.vertical, 2)
                .background(PanelPalette.surface)
                .clipShape(RoundedRectangle(cornerRadius: 4, style: .continuous))

            Spacer(minLength: 4)

            Button {
                viewModel.openSettings()
            } label: {
                Image(systemName: "gearshape")
                    .font(.system(size: 12))
                    .foregroundColor(PanelPalette.secondaryText)
            }
            .buttonStyle(.plain)
            .help("Settings")

            Button {
                viewModel.closePanel(restoreFocus: true)
            } label: {
                Image(systemName: "xmark")
                    .font(.system(size: 11, weight: .semibold))
                    .foregroundColor(PanelPalette.secondaryText)
            }
            .buttonStyle(.plain)
            .help("Close (Esc)")
        }
        .padding(.horizontal, 12)
        .padding(.vertical, 10)
    }

    @ViewBuilder
    private var contentArea: some View {
        switch viewModel.stage {
        case .chooseAction:
            chooseActionStage
        case .promptInput(let action):
            promptInputStage(for: action)
        case .streaming(let action):
            streamingStage(for: action)
        case .previewReady(let action):
            previewReadyStage(for: action)
        case .error(let action, let message, let retryable):
            errorStage(for: action, message: message, retryable: retryable)
        }
    }

    private var chooseActionStage: some View {
        VStack(alignment: .leading, spacing: 8) {
            if viewModel.isExpanded {
                HStack(spacing: 6) {
                    Image(systemName: "magnifyingglass")
                        .font(.system(size: 11))
                        .foregroundColor(PanelPalette.secondaryText)
                    TextField("Filter actions…", text: $viewModel.filterText)
                        .textFieldStyle(.plain)
                        .font(.system(size: 12))
                        .foregroundColor(PanelPalette.primaryText)
                }
                .padding(.horizontal, 8)
                .padding(.vertical, 6)
                .background(PanelPalette.surface)
                .clipShape(RoundedRectangle(cornerRadius: 6, style: .continuous))
                .padding(.horizontal, 10)
                .padding(.top, 8)
            }

            ScrollView {
                VStack(alignment: .leading, spacing: 4) {
                    if viewModel.isExpanded {
                        let flatActions = viewModel.visibleActions
                        if viewModel.filteredGroups.isEmpty {
                            Text("No matching actions.")
                                .font(.system(size: 12))
                                .foregroundColor(PanelPalette.secondaryText)
                                .padding(.horizontal, 12)
                                .padding(.vertical, 12)
                        } else {
                            ForEach(viewModel.filteredGroups) { group in
                                Text(group.label.uppercased())
                                    .font(.system(size: 10, weight: .semibold))
                                    .foregroundColor(PanelPalette.secondaryText)
                                    .padding(.horizontal, 12)
                                    .padding(.top, 6)

                                ForEach(group.items) { action in
                                    let idx = flatActions.firstIndex(of: action) ?? 0
                                    actionRow(action: action, index: idx)
                                }
                            }
                        }
                    } else {
                        let actions = viewModel.visibleActions
                        ForEach(Array(actions.enumerated()), id: \.element.id) { idx, action in
                            actionRow(action: action, index: idx)
                        }
                    }
                }
                .padding(.horizontal, 8)
                .padding(.vertical, 6)
            }
            .frame(maxHeight: 280)

            Divider().background(PanelPalette.border)

            HStack {
                Button {
                    viewModel.isExpanded.toggle()
                    viewModel.filterText = ""
                    viewModel.selectedIndex = 0
                } label: {
                    Text(viewModel.isExpanded ? "Compact (Tab)" : "All actions (Tab)")
                        .font(.system(size: 11, weight: .medium))
                        .foregroundColor(PanelPalette.accent)
                }
                .buttonStyle(.plain)

                Spacer()

                Text("1–\(min(9, viewModel.visibleActions.count)) run · ↑↓ · Esc")
                    .font(.system(size: 10))
                    .foregroundColor(PanelPalette.secondaryText)
            }
            .padding(.horizontal, 12)
            .padding(.bottom, 9)
            .padding(.top, 2)
        }
    }

    private func actionRow(action: MenuAction, index: Int) -> some View {
        let isSelected = index == viewModel.selectedIndex
        return HStack(spacing: 8) {
            if index < 9 {
                Text("\(index + 1)")
                    .font(.system(size: 10, weight: .semibold, design: .monospaced))
                    .foregroundColor(isSelected ? .white : PanelPalette.secondaryText)
                    .frame(width: 18, height: 18)
                    .background(isSelected ? PanelPalette.accent : PanelPalette.surface)
                    .clipShape(RoundedRectangle(cornerRadius: 4, style: .continuous))
            }

            VStack(alignment: .leading, spacing: 1) {
                Text(action.label)
                    .font(.system(size: 12, weight: .medium))
                    .foregroundColor(PanelPalette.primaryText)
                Text(action.description)
                    .font(.system(size: 10))
                    .foregroundColor(PanelPalette.secondaryText)
                    .lineLimit(1)
            }

            Spacer(minLength: 4)

            if action.options == .language {
                Menu {
                    ForEach(MenuModel.languages(including: viewModel.selectedLanguage), id: \.self) { lang in
                        Button(lang) {
                            viewModel.selectLanguage(lang)
                        }
                    }
                } label: {
                    Text(MenuModel.languageCode(for: viewModel.selectedLanguage))
                        .font(.system(size: 10, weight: .semibold, design: .monospaced))
                        .foregroundColor(PanelPalette.primaryText)
                        .padding(.horizontal, 6)
                        .padding(.vertical, 3)
                        .background(PanelPalette.surfaceHover)
                        .clipShape(RoundedRectangle(cornerRadius: 4, style: .continuous))
                }
                .menuStyle(.borderlessButton)
                .fixedSize()
            } else if action.options == .context && viewModel.snapshot.surroundingContext.hasContext {
                Button {
                    viewModel.toggleContext(for: action.id)
                } label: {
                    Text(viewModel.includeContext ? "CTX ON" : "CTX OFF")
                        .font(.system(size: 9, weight: .semibold, design: .monospaced))
                        .foregroundColor(viewModel.includeContext ? .white : PanelPalette.secondaryText)
                        .padding(.horizontal, 5)
                        .padding(.vertical, 2)
                        .background(viewModel.includeContext ? PanelPalette.accent : PanelPalette.surfaceHover)
                        .clipShape(RoundedRectangle(cornerRadius: 4, style: .continuous))
                }
                .buttonStyle(.plain)
            }
        }
        .padding(.horizontal, 8)
        .padding(.vertical, 6)
        .background(isSelected ? PanelPalette.surfaceHover : Color.clear)
        .clipShape(RoundedRectangle(cornerRadius: 7, style: .continuous))
        .contentShape(Rectangle())
        .onTapGesture {
            viewModel.selectedIndex = index
            viewModel.selectAction(action)
        }
    }

    private func promptInputStage(for action: MenuAction) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(action.label)
                .font(.system(size: 12, weight: .semibold))
                .foregroundColor(PanelPalette.primaryText)

            TextField(
                action.id == MenuModel.compose.id ? "What should Emendly write?" : "Enter your instruction…",
                text: $viewModel.promptInputText,
                axis: .vertical
            )
            .textFieldStyle(.plain)
            .font(.system(size: 12))
            .foregroundColor(PanelPalette.primaryText)
            .lineLimit(3...6)
            .padding(8)
            .background(PanelPalette.surface)
            .clipShape(RoundedRectangle(cornerRadius: 7, style: .continuous))
            .overlay(
                RoundedRectangle(cornerRadius: 7, style: .continuous)
                    .stroke(PanelPalette.border, lineWidth: 1)
            )

            if action.options == .context && viewModel.snapshot.surroundingContext.hasContext {
                Toggle("Include surrounding context", isOn: $viewModel.includeContext)
                    .toggleStyle(.checkbox)
                    .font(.system(size: 11))
                    .foregroundColor(PanelPalette.secondaryText)
            }

            if let msg = viewModel.statusMessage {
                Text(msg)
                    .font(.system(size: 11))
                    .foregroundColor(PanelPalette.danger)
            }

            HStack {
                Button("Back") {
                    viewModel.goBackToChooseAction()
                }
                .buttonStyle(.bordered)

                Spacer()

                Button("Run") {
                    viewModel.submitPromptInput(for: action)
                }
                .buttonStyle(.borderedProminent)
                .tint(PanelPalette.accent)
            }
        }
        .padding(12)
    }

    private func streamingStage(for action: MenuAction) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 8) {
                ProgressView()
                    .controlSize(.small)
                Text("\(action.label)…")
                    .font(.system(size: 12, weight: .medium))
                    .foregroundColor(PanelPalette.primaryText)
                Spacer()
            }

            ScrollView {
                Text(viewModel.outputText.isEmpty ? "Waiting for response…" : viewModel.outputText)
                    .font(.system(size: 12))
                    .foregroundColor(viewModel.outputText.isEmpty ? PanelPalette.secondaryText : PanelPalette.primaryText)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .textSelection(.enabled)
            }
            .frame(minHeight: 80, maxHeight: 220)
            .padding(8)
            .background(PanelPalette.surface)
            .clipShape(RoundedRectangle(cornerRadius: 7, style: .continuous))

            HStack {
                Spacer()
                Button("Cancel") {
                    viewModel.goBackToChooseAction()
                }
                .buttonStyle(.bordered)
            }
        }
        .padding(12)
    }

    private func previewReadyStage(for action: MenuAction) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text(action.label)
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundColor(PanelPalette.primaryText)
                Spacer()
                if viewModel.isCopyOnly(for: action) {
                    Text(viewModel.snapshot.isReadOnly ? "Read-only field" : "Reference")
                        .font(.system(size: 10))
                        .foregroundColor(PanelPalette.secondaryText)
                }
            }

            ScrollView {
                Text(viewModel.outputText)
                    .font(.system(size: 12))
                    .foregroundColor(PanelPalette.primaryText)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .textSelection(.enabled)
            }
            .frame(minHeight: 80, maxHeight: 240)
            .padding(8)
            .background(PanelPalette.surface)
            .clipShape(RoundedRectangle(cornerRadius: 7, style: .continuous))

            HStack(spacing: 8) {
                Button("Back") {
                    viewModel.goBackToChooseAction()
                }
                .buttonStyle(.bordered)

                Spacer()

                if !viewModel.isCopyOnly(for: action) {
                    Button("Copy") {
                        viewModel.copyResult()
                    }
                    .buttonStyle(.bordered)
                }

                Button("\(viewModel.primaryButtonTitle(for: action)) (↵)") {
                    viewModel.acceptResult(for: action)
                }
                .buttonStyle(.borderedProminent)
                .tint(PanelPalette.accent)
            }
        }
        .padding(12)
    }

    private func errorStage(for action: MenuAction, message: String, retryable: Bool) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack(spacing: 6) {
                Image(systemName: "exclamationmark.triangle.fill")
                    .foregroundColor(PanelPalette.danger)
                Text("\(action.label) failed")
                    .font(.system(size: 12, weight: .semibold))
                    .foregroundColor(PanelPalette.primaryText)
            }

            Text(message)
                .font(.system(size: 12))
                .foregroundColor(PanelPalette.secondaryText)
                .fixedSize(horizontal: false, vertical: true)
                .padding(8)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(PanelPalette.surface)
                .clipShape(RoundedRectangle(cornerRadius: 7, style: .continuous))

            HStack {
                Button("Back") {
                    viewModel.goBackToChooseAction()
                }
                .buttonStyle(.bordered)

                Spacer()

                Button("Retry") {
                    viewModel.retry(action)
                }
                .buttonStyle(.borderedProminent)
                .tint(retryable ? PanelPalette.accent : PanelPalette.surfaceHover)
            }
        }
        .padding(12)
    }
}
