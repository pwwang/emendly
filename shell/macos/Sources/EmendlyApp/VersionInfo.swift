import Combine
import Foundation

@MainActor
public final class VersionInfo: ObservableObject {
    public static let shared = VersionInfo()
    public static let protocolVersion = 1

    public let appVersion: String
    @Published public var engineVersion: String?

    private init() {
        if let short = Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String,
           !short.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        {
            self.appVersion = short.trimmingCharacters(in: .whitespacesAndNewlines)
        } else {
            self.appVersion = "0.1.0"
        }
    }

    public var formattedFooter: String {
        let trimmed = engineVersion?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        let engineText = trimmed.isEmpty ? "—" : trimmed
        return "Emendly \(appVersion) · engine \(engineText) · protocol \(Self.protocolVersion)"
    }
}
