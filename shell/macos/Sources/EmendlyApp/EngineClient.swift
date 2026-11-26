import Foundation

public actor EngineClient {
    public static let supportedProtocolVersion = VersionInfo.protocolVersion
    private static let handshakeTimeoutNs: UInt64 = 5_000_000_000

    private enum PendingRequest {
        case health(CheckedContinuation<EngineHealthInfo, Error>)
        case actions(CheckedContinuation<[EngineActionInfo], Error>)
        case config(CheckedContinuation<EngineConfig, Error>)
        case run(
            onDelta: @MainActor @Sendable (String) -> Void,
            accumulated: String,
            continuation: CheckedContinuation<EngineRunResult, Error>
        )

        func fail(with error: Error) {
            switch self {
            case .health(let c): c.resume(throwing: error)
            case .actions(let c): c.resume(throwing: error)
            case .config(let c): c.resume(throwing: error)
            case .run(_, _, let c): c.resume(throwing: error)
            }
        }
    }

    private var process: Process?
    private var stdinPipe: Pipe?
    private var stdoutTask: Task<Void, Never>?
    private var spawnedApiKey: String?
    private var nextRequestCounter: UInt64 = 0
    private var pending: [String: PendingRequest] = [:]
    private var activeRunRequestId: String?

    private var baseUrl: String
    private var model: String
    private var defaultTargetLanguage: String
    public private(set) var engineVersion: String?
    public private(set) var resolvedEnginePath: String?

    public init(
        baseUrl: String = AppSettings.defaultBaseUrl,
        model: String = AppSettings.defaultModel,
        defaultTargetLanguage: String = LanguageSettings.defaultTargetLanguage
    ) {
        self.baseUrl = baseUrl
        self.model = model
        self.defaultTargetLanguage = defaultTargetLanguage
        self.resolvedEnginePath = Self.resolveEnginePath()
    }

    public static func resolveEnginePath() -> String? {
        let fm = FileManager.default

        if let envPath = ProcessInfo.processInfo.environment["EMENDLY_ENGINE"]?
            .trimmingCharacters(in: .whitespacesAndNewlines),
           !envPath.isEmpty,
           fm.isExecutableFile(atPath: envPath)
        {
            return envPath
        }

        if let resourcePath = Bundle.main.resourcePath {
            let bundled = URL(fileURLWithPath: resourcePath)
                .appendingPathComponent("engine/emendly-engine")
                .path
            if fm.isExecutableFile(atPath: bundled) {
                return bundled
            }
        }

        if let executableURL = Bundle.main.executableURL {
            let relativeResources = executableURL
                .deletingLastPathComponent()
                .appendingPathComponent("../Resources/engine/emendly-engine")
                .standardized
                .path
            if fm.isExecutableFile(atPath: relativeResources) {
                return relativeResources
            }

            if let repoMatch = findRepoEngine(startingFrom: executableURL.deletingLastPathComponent()) {
                return repoMatch
            }
        }

        let cwd = URL(fileURLWithPath: fm.currentDirectoryPath, isDirectory: true)
        if let repoMatch = findRepoEngine(startingFrom: cwd) {
            return repoMatch
        }

        return nil
    }

    private static func findRepoEngine(startingFrom start: URL) -> String? {
        let fm = FileManager.default
        var cursor = start.standardized
        for _ in 0..<8 {
            for rel in ["engine/target/release/emendly-engine", "engine/target/debug/emendly-engine"] {
                let candidate = cursor.appendingPathComponent(rel).path
                if fm.isExecutableFile(atPath: candidate) {
                    return candidate
                }
            }
            let parent = cursor.deletingLastPathComponent()
            if parent.path == cursor.path {
                break
            }
            cursor = parent
        }
        return nil
    }

    public func warmUp(baseUrl: String, model: String, defaultTargetLanguage: String) async throws {
        self.baseUrl = baseUrl
        self.model = model
        self.defaultTargetLanguage = defaultTargetLanguage
        try await ensureStarted()
    }

    public func configure(baseUrl: String, model: String, defaultTargetLanguage: String) async throws {
        self.baseUrl = baseUrl
        self.model = model
        self.defaultTargetLanguage = defaultTargetLanguage

        let currentKey = KeychainHelper.resolveApiKey().key
        if let proc = process, proc.isRunning, spawnedApiKey == currentKey {
            _ = try await sendConfigSet()
        } else {
            try await ensureStarted()
        }
    }

    public func listActions() async throws -> [EngineActionInfo] {
        try await ensureStarted()
        let id = nextId()
        return try await withCheckedThrowingContinuation { continuation in
            pending[id] = .actions(continuation)
            do {
                try writeJSONLine(EngineActionsListRequest(id: id))
            } catch {
                if let entry = pending.removeValue(forKey: id) {
                    entry.fail(with: error)
                }
            }
        }
    }

    public func runAction(
        action: String,
        text: String,
        options: EngineActionOptions,
        context: String? = nil,
        app: String? = nil,
        onDelta: @escaping @MainActor @Sendable (String) -> Void
    ) async throws -> EngineRunResult {
        try await ensureStarted()
        let id = nextId()
        activeRunRequestId = id

        let request = EngineActionsRunRequest(
            id: id,
            action: action,
            text: text,
            options: options,
            context: context,
            app: app,
            stream: true
        )

        return try await withCheckedThrowingContinuation { continuation in
            pending[id] = .run(onDelta: onDelta, accumulated: "", continuation: continuation)
            do {
                try writeJSONLine(request)
            } catch {
                if activeRunRequestId == id {
                    activeRunRequestId = nil
                }
                if let entry = pending.removeValue(forKey: id) {
                    entry.fail(with: error)
                }
            }
        }
    }

    public func cancelActiveRun() {
        guard let targetId = activeRunRequestId else {
            return
        }
        cancel(requestId: targetId)
    }

    public func cancel(requestId: String) {
        let cancelId = nextId()
        try? writeJSONLine(EngineCancelRequest(id: cancelId, target: requestId))
        Task { [weak self] in
            try? await Task.sleep(nanoseconds: 600_000_000)
            await self?.expirePendingRequest(id: requestId, error: EngineClientError.cancelled)
        }
    }

    public func shutdown() {
        if let proc = process, proc.isRunning {
            let id = nextId()
            try? writeJSONLine(EngineShutdownRequest(id: id))
            try? stdinPipe?.fileHandleForWriting.close()

            let deadline = Date().addingTimeInterval(0.6)
            while proc.isRunning && Date() < deadline {
                Thread.sleep(forTimeInterval: 0.02)
            }
            if proc.isRunning {
                proc.terminate()
            }
        }
        cleanupProcess(exitError: EngineClientError.cancelled)
    }

    // MARK: - Lifecycle & Wire Handling

    private func ensureStarted() async throws {
        let resolvedKey = KeychainHelper.resolveApiKey().key

        if let proc = process, proc.isRunning {
            if spawnedApiKey == resolvedKey {
                return
            }
            shutdown()
        }

        guard let enginePath = Self.resolveEnginePath() else {
            resolvedEnginePath = nil
            throw EngineClientError.binaryNotFound
        }
        resolvedEnginePath = enginePath

        try launchProcess(at: enginePath, apiKey: resolvedKey)

        let health = try await performHealthCheck()
        guard health.protocolVersion == Self.supportedProtocolVersion else {
            shutdown()
            throw EngineClientError.unsupportedProtocol(
                expected: Self.supportedProtocolVersion,
                actual: health.protocolVersion
            )
        }

        engineVersion = health.version
        await MainActor.run {
            VersionInfo.shared.engineVersion = health.version
        }

        _ = try await sendConfigSet()
    }

    private func launchProcess(at path: String, apiKey: String?) throws {
        let proc = Process()
        let engineURL = URL(fileURLWithPath: path)
        proc.executableURL = engineURL
        proc.currentDirectoryURL = engineURL.deletingLastPathComponent()

        var env = ProcessInfo.processInfo.environment
        if let apiKey, !apiKey.isEmpty {
            env[KeychainHelper.envKeyName] = apiKey
        } else {
            env.removeValue(forKey: KeychainHelper.envKeyName)
        }
        proc.environment = env

        let inPipe = Pipe()
        let outPipe = Pipe()
        let errPipe = Pipe()
        proc.standardInput = inPipe
        proc.standardOutput = outPipe
        proc.standardError = errPipe

        // Drain stderr so the engine can never block on a full pipe buffer.
        errPipe.fileHandleForReading.readabilityHandler = { handle in
            _ = handle.availableData
        }

        do {
            try proc.run()
        } catch {
            errPipe.fileHandleForReading.readabilityHandler = nil
            throw EngineClientError.launchFailed(error.localizedDescription)
        }

        self.process = proc
        self.stdinPipe = inPipe
        self.spawnedApiKey = apiKey

        let launchedPid = proc.processIdentifier
        let lineStream = makeLineStream(from: outPipe.fileHandleForReading)
        self.stdoutTask = Task { [weak self] in
            for await line in lineStream {
                guard let self else { break }
                await self.handleStdoutLine(line)
            }
            guard let self else { return }
            await self.handleProcessEOF(launchedPid: launchedPid)
        }
    }

    private nonisolated func makeLineStream(from handle: FileHandle) -> AsyncStream<String> {
        AsyncStream { continuation in
            let queue = DispatchQueue(label: "dev.emendly.engine.stdout", qos: .userInitiated)
            queue.async {
                var buffer = Data()
                while true {
                    let chunk = handle.availableData
                    if chunk.isEmpty {
                        if !buffer.isEmpty,
                           let remainder = String(data: buffer, encoding: .utf8)?
                            .trimmingCharacters(in: .whitespacesAndNewlines),
                           !remainder.isEmpty
                        {
                            continuation.yield(remainder)
                        }
                        continuation.finish()
                        return
                    }

                    buffer.append(chunk)
                    while let newlineIndex = buffer.firstIndex(of: 0x0A) {
                        let lineData = buffer.prefix(upTo: newlineIndex)
                        buffer.removeSubrange(...newlineIndex)
                        if let line = String(data: lineData, encoding: .utf8)?
                            .trimmingCharacters(in: .whitespacesAndNewlines),
                           !line.isEmpty
                        {
                            continuation.yield(line)
                        }
                    }
                }
            }
        }
    }

    private func performHealthCheck() async throws -> EngineHealthInfo {
        let id = nextId()
        return try await withCheckedThrowingContinuation { continuation in
            pending[id] = .health(continuation)
            do {
                try writeJSONLine(EngineHealthRequest(id: id))
            } catch {
                if let entry = pending.removeValue(forKey: id) {
                    entry.fail(with: error)
                }
                return
            }

            Task { [weak self] in
                try? await Task.sleep(nanoseconds: Self.handshakeTimeoutNs)
                await self?.expirePendingRequest(id: id, error: EngineClientError.handshakeTimedOut)
            }
        }
    }

    private func sendConfigSet() async throws -> EngineConfig {
        let id = nextId()
        let patch = EngineConfigPatch(
            baseUrl: baseUrl.isEmpty ? nil : baseUrl,
            model: model.isEmpty ? nil : model,
            defaultTarget: defaultTargetLanguage.isEmpty ? nil : defaultTargetLanguage
        )
        return try await withCheckedThrowingContinuation { continuation in
            pending[id] = .config(continuation)
            do {
                try writeJSONLine(EngineConfigSetRequest(id: id, patch: patch))
            } catch {
                if let entry = pending.removeValue(forKey: id) {
                    entry.fail(with: error)
                }
                return
            }

            Task { [weak self] in
                try? await Task.sleep(nanoseconds: Self.handshakeTimeoutNs)
                await self?.expirePendingRequest(id: id, error: EngineClientError.handshakeTimedOut)
            }
        }
    }

    private func expirePendingRequest(id: String, error: Error) {
        if let entry = pending.removeValue(forKey: id) {
            entry.fail(with: error)
        }
    }

    private func handleStdoutLine(_ line: String) async {
        guard let event = EngineEvent.decode(from: line) else {
            return
        }

        guard let entry = pending[event.id] else {
            return
        }

        switch event.kind {
        case .health(let version, let proto):
            pending.removeValue(forKey: event.id)
            if case .health(let continuation) = entry {
                continuation.resume(returning: EngineHealthInfo(version: version, protocolVersion: proto))
            }

        case .actions(let list):
            pending.removeValue(forKey: event.id)
            if case .actions(let continuation) = entry {
                continuation.resume(returning: list)
            }

        case .config(let config):
            pending.removeValue(forKey: event.id)
            if case .config(let continuation) = entry {
                continuation.resume(returning: config)
            }

        case .started:
            break

        case .delta(let text):
            if case .run(let onDelta, let accumulated, let continuation) = entry {
                pending[event.id] = .run(
                    onDelta: onDelta,
                    accumulated: accumulated + text,
                    continuation: continuation
                )
                await MainActor.run {
                    onDelta(text)
                }
            }

        case .done(let fullText, let usage, let latencyMs):
            pending.removeValue(forKey: event.id)
            if activeRunRequestId == event.id {
                activeRunRequestId = nil
            }
            if case .run(_, let accumulated, let continuation) = entry {
                let resolved = fullText.isEmpty ? accumulated : fullText
                continuation.resume(returning: EngineRunResult(text: resolved, usage: usage, latencyMs: latencyMs))
            }

        case .cancelled:
            pending.removeValue(forKey: event.id)
            if activeRunRequestId == event.id {
                activeRunRequestId = nil
            }
            entry.fail(with: EngineClientError.cancelled)

        case .error(let code, let message, let retryable):
            pending.removeValue(forKey: event.id)
            if activeRunRequestId == event.id {
                activeRunRequestId = nil
            }
            entry.fail(with: EngineClientError.engineError(code: code, message: message, retryable: retryable))
        }
    }

    private func handleProcessEOF(launchedPid: Int32) {
        guard let current = process, current.processIdentifier == launchedPid else {
            return
        }
        let status = current.isRunning ? Int32(-1) : current.terminationStatus
        cleanupProcess(exitError: EngineClientError.processExited(status))
    }

    private func cleanupProcess(exitError: Error) {
        stdoutTask?.cancel()
        stdoutTask = nil

        if let errPipe = process?.standardError as? Pipe {
            errPipe.fileHandleForReading.readabilityHandler = nil
        }

        try? stdinPipe?.fileHandleForWriting.close()
        stdinPipe = nil
        process = nil
        spawnedApiKey = nil
        activeRunRequestId = nil

        let remaining = pending
        pending.removeAll()
        for (_, request) in remaining {
            request.fail(with: exitError)
        }
    }

    private func writeJSONLine<T: Encodable>(_ payload: T) throws {
        guard let handle = stdinPipe?.fileHandleForWriting else {
            throw EngineClientError.writeFailed("Engine stdin is not open.")
        }

        var data = try JSONEncoder().encode(payload)
        data.append(0x0A)
        do {
            try handle.write(contentsOf: data)
        } catch {
            throw EngineClientError.writeFailed(error.localizedDescription)
        }
    }

    private func nextId() -> String {
        nextRequestCounter &+= 1
        return "r\(nextRequestCounter)"
    }
}
