import AppKit
import Foundation

public enum UpdateCheckResult: Sendable, Equatable {
    case upToDate(currentVersion: String)
    case updateAvailable(latestVersion: String, downloadURL: URL, releaseURL: URL)
    case failed(String)
}

public enum UpdateChecker {
    public static let latestReleaseEndpoint = URL(
        string: "https://api.github.com/repos/pwwang/emendly/releases/latest"
    )!

    private struct GitHubRelease: Decodable {
        struct Asset: Decodable {
            let name: String
            let browserDownloadUrl: String

            enum CodingKeys: String, CodingKey {
                case name
                case browserDownloadUrl = "browser_download_url"
            }
        }

        let tagName: String
        let htmlUrl: String
        let assets: [Asset]

        enum CodingKeys: String, CodingKey {
            case tagName = "tag_name"
            case htmlUrl = "html_url"
            case assets
        }
    }

    public static func check(currentVersion: String) async -> UpdateCheckResult {
        var request = URLRequest(url: latestReleaseEndpoint)
        request.httpMethod = "GET"
        request.timeoutInterval = 10
        request.setValue("Emendly/\(currentVersion)", forHTTPHeaderField: "User-Agent")
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")

        do {
            let (data, response) = try await URLSession.shared.data(for: request)
            guard let http = response as? HTTPURLResponse else {
                return .failed("Unexpected response while checking for updates.")
            }

            guard (200..<300).contains(http.statusCode) else {
                return .failed("Update check failed (HTTP \(http.statusCode)).")
            }

            let release = try JSONDecoder().decode(GitHubRelease.self, from: data)
            let latest = normalizeVersion(release.tagName)
            let current = normalizeVersion(currentVersion)

            guard isVersion(latest, newerThan: current) else {
                return .upToDate(currentVersion: current)
            }

            guard let releaseURL = URL(string: release.htmlUrl) else {
                return .failed("Invalid release URL returned by GitHub.")
            }

            let dmgAssetURL = release.assets
                .first(where: { $0.name.lowercased().hasSuffix("-macos.dmg") })
                .flatMap { URL(string: $0.browserDownloadUrl) }

            return .updateAvailable(
                latestVersion: latest,
                downloadURL: dmgAssetURL ?? releaseURL,
                releaseURL: releaseURL
            )
        } catch {
            return .failed(error.localizedDescription)
        }
    }

    @MainActor
    public static func open(_ url: URL) {
        _ = NSWorkspace.shared.open(url)
    }

    public static func normalizeVersion(_ raw: String) -> String {
        var trimmed = raw.trimmingCharacters(in: .whitespacesAndNewlines)
        if trimmed.hasPrefix("v") || trimmed.hasPrefix("V") {
            trimmed.removeFirst()
        }
        if let plus = trimmed.firstIndex(of: "+") {
            trimmed = String(trimmed[..<plus])
        }
        return trimmed
    }

    public static func isVersion(_ candidate: String, newerThan current: String) -> Bool {
        let a = versionComponents(candidate)
        let b = versionComponents(current)
        let count = max(a.count, b.count)
        for i in 0..<count {
            let av = i < a.count ? a[i] : 0
            let bv = i < b.count ? b[i] : 0
            if av != bv {
                return av > bv
            }
        }
        return false
    }

    private static func versionComponents(_ version: String) -> [Int] {
        let core = version.split(separator: "-", maxSplits: 1).first.map(String.init) ?? version
        return core.split(separator: ".").map { Int($0) ?? 0 }
    }
}
