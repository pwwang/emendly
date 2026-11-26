import Foundation

public struct EngineActionOptions: Codable, Sendable, Equatable {
    public var includeContext: Bool
    public var contextBefore: String?
    public var contextAfter: String?
    public var targetLang: String?
    public var instruction: String?
    public var tone: String?
    public var brief: String?
    public var context: String?

    enum CodingKeys: String, CodingKey {
        case includeContext = "include_context"
        case contextBefore = "context_before"
        case contextAfter = "context_after"
        case targetLang = "target_lang"
        case instruction
        case tone
        case brief
        case context
    }

    public init(
        includeContext: Bool = false,
        contextBefore: String? = nil,
        contextAfter: String? = nil,
        targetLang: String? = nil,
        instruction: String? = nil,
        tone: String? = nil,
        brief: String? = nil,
        context: String? = nil
    ) {
        self.includeContext = includeContext
        self.contextBefore = contextBefore
        self.contextAfter = contextAfter
        self.targetLang = targetLang
        self.instruction = instruction
        self.tone = tone
        self.brief = brief
        self.context = context
    }
}

public struct EngineConfigPatch: Codable, Sendable, Equatable {
    public var baseUrl: String?
    public var model: String?
    public var timeoutMs: UInt64?
    public var defaultTarget: String?

    enum CodingKeys: String, CodingKey {
        case baseUrl = "base_url"
        case model
        case timeoutMs = "timeout_ms"
        case defaultTarget = "default_target"
    }

    public init(
        baseUrl: String? = nil,
        model: String? = nil,
        timeoutMs: UInt64? = nil,
        defaultTarget: String? = nil
    ) {
        self.baseUrl = baseUrl
        self.model = model
        self.timeoutMs = timeoutMs
        self.defaultTarget = defaultTarget
    }
}

public struct EngineHealthRequest: Encodable, Sendable {
    public let id: String
    public let type = "health"
}

public struct EngineActionsListRequest: Encodable, Sendable {
    public let id: String
    public let type = "actions.list"
}

public struct EngineConfigGetRequest: Encodable, Sendable {
    public let id: String
    public let type = "config.get"
}

public struct EngineConfigSetRequest: Encodable, Sendable {
    public let id: String
    public let type = "config.set"
    public let patch: EngineConfigPatch
}

public struct EngineActionsRunRequest: Encodable, Sendable {
    public let id: String
    public let type = "actions.run"
    public let action: String
    public let text: String
    public let options: EngineActionOptions
    public let context: String?
    public let app: String?
    public let stream: Bool
}

public struct EngineCancelRequest: Encodable, Sendable {
    public let id: String
    public let type = "cancel"
    public let target: String
}

public struct EngineShutdownRequest: Encodable, Sendable {
    public let id: String
    public let type = "shutdown"
}

public struct EngineUsage: Codable, Sendable, Equatable {
    public let inputTokens: UInt64
    public let outputTokens: UInt64

    enum CodingKeys: String, CodingKey {
        case inputTokens = "input_tokens"
        case outputTokens = "output_tokens"
    }

    public init(inputTokens: UInt64 = 0, outputTokens: UInt64 = 0) {
        self.inputTokens = inputTokens
        self.outputTokens = outputTokens
    }
}

public struct EngineActionInfo: Codable, Sendable, Equatable {
    public let name: String
    public let description: String
    public let requiresText: Bool
    public let maxTokens: UInt32?

    enum CodingKeys: String, CodingKey {
        case name
        case description
        case requiresText = "requires_text"
        case maxTokens = "max_tokens"
    }
}

public struct EngineProviderConfig: Codable, Sendable, Equatable {
    public let baseUrl: String
    public let model: String
    public let timeoutMs: UInt64

    enum CodingKeys: String, CodingKey {
        case baseUrl = "base_url"
        case model
        case timeoutMs = "timeout_ms"
    }
}

public struct EngineLanguageConfig: Codable, Sendable, Equatable {
    public let ui: String
    public let defaultTarget: String

    enum CodingKeys: String, CodingKey {
        case ui
        case defaultTarget = "default_target"
    }
}

public struct EngineLoggingConfig: Codable, Sendable, Equatable {
    public let level: String
    public let path: String
}

public struct EngineConfig: Codable, Sendable, Equatable {
    public let provider: EngineProviderConfig
    public let language: EngineLanguageConfig
    public let logging: EngineLoggingConfig
}

public enum EngineErrorCode: String, Codable, Sendable, Equatable {
    case badRequest = "bad_request"
    case unknownAction = "unknown_action"
    case missingKey = "missing_key"
    case providerAuth = "provider_auth"
    case providerRateLimit = "provider_rate_limit"
    case providerError = "provider_error"
    case network = "network"
    case timeout = "timeout"
    case cancelled = "cancelled"
    case `internal` = "internal"
}

public struct EngineRunResult: Sendable, Equatable {
    public let text: String
    public let usage: EngineUsage
    public let latencyMs: UInt64
}

public struct EngineHealthInfo: Sendable, Equatable {
    public let version: String
    public let protocolVersion: Int
}

public enum EngineEventKind: Sendable {
    case health(version: String, protocolVersion: Int)
    case actions([EngineActionInfo])
    case config(EngineConfig)
    case started(action: String, model: String)
    case delta(String)
    case done(text: String, usage: EngineUsage, latencyMs: UInt64)
    case cancelled
    case error(code: EngineErrorCode, message: String, retryable: Bool)
}

public struct EngineEvent: Sendable {
    public let id: String
    public let kind: EngineEventKind

    private struct RawEvent: Decodable {
        let id: String
        let event: String
        let version: String?
        let `protocol`: Int?
        let actions: [EngineActionInfo]?
        let config: EngineConfig?
        let action: String?
        let model: String?
        let text: String?
        let usage: EngineUsage?
        let latencyMs: UInt64?
        let code: String?
        let message: String?
        let retryable: Bool?

        enum CodingKeys: String, CodingKey {
            case id, event, version, `protocol`, actions, config, action, model, text, usage
            case latencyMs = "latency_ms"
            case code, message, retryable
        }
    }

    public static func decode(from line: String) -> EngineEvent? {
        guard let data = line.data(using: .utf8),
              let raw = try? JSONDecoder().decode(RawEvent.self, from: data)
        else {
            return nil
        }

        let kind: EngineEventKind
        switch raw.event {
        case "health":
            kind = .health(version: raw.version ?? "", protocolVersion: raw.protocol ?? 0)
        case "actions":
            kind = .actions(raw.actions ?? [])
        case "config":
            guard let config = raw.config else { return nil }
            kind = .config(config)
        case "started":
            kind = .started(action: raw.action ?? "", model: raw.model ?? "")
        case "delta":
            kind = .delta(raw.text ?? "")
        case "done":
            kind = .done(
                text: raw.text ?? "",
                usage: raw.usage ?? EngineUsage(),
                latencyMs: raw.latencyMs ?? 0
            )
        case "cancelled":
            kind = .cancelled
        case "error":
            let code = raw.code.flatMap(EngineErrorCode.init(rawValue:)) ?? .internal
            kind = .error(
                code: code,
                message: raw.message ?? "Engine returned an error.",
                retryable: raw.retryable ?? false
            )
        default:
            return nil
        }

        return EngineEvent(id: raw.id, kind: kind)
    }
}

public enum EngineClientError: LocalizedError, Sendable {
    case binaryNotFound
    case launchFailed(String)
    case unsupportedProtocol(expected: Int, actual: Int)
    case engineError(code: EngineErrorCode, message: String, retryable: Bool)
    case cancelled
    case processExited(Int32)
    case writeFailed(String)
    case handshakeTimedOut

    public var isRetryable: Bool {
        switch self {
        case .engineError(_, _, let retryable):
            return retryable
        case .processExited, .handshakeTimedOut:
            return true
        default:
            return false
        }
    }

    public var errorDescription: String? {
        switch self {
        case .binaryNotFound:
            return "Could not find emendly-engine. Build the engine or set EMENDLY_ENGINE."
        case .launchFailed(let details):
            return "Failed to launch emendly-engine: \(details)"
        case .unsupportedProtocol(let expected, let actual):
            return "Unsupported engine protocol version \(actual) (expected \(expected))."
        case .engineError(let code, let message, _):
            if code == .missingKey {
                return "No API key configured. Open Settings to save a key in macOS Keychain or set EMENDLY_API_KEY."
            }
            return message
        case .cancelled:
            return "Request cancelled."
        case .processExited(let status):
            return "Engine process exited unexpectedly (code \(status))."
        case .writeFailed(let details):
            return "Failed to communicate with engine: \(details)"
        case .handshakeTimedOut:
            return "Timed out waiting for emendly-engine to respond."
        }
    }
}
