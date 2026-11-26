import CoreGraphics
import XCTest
@testable import EmendlyCapture

final class SelectionClassifierTests: XCTestCase {
    // MARK: - SelectionClassifier

    func testEmptyAndWhitespaceSelectionsClassifyAsEmpty() {
        let nilResult = SelectionClassifier.classify(nil)
        XCTAssertEqual(nilResult.state, .empty)
        XCTAssertEqual(nilResult.text, "")
        XCTAssertEqual(nilResult.wordCount, 0)
        XCTAssertFalse(nilResult.isReadOnly)

        let emptyResult = SelectionClassifier.classify("")
        XCTAssertEqual(emptyResult.state, .empty)
        XCTAssertEqual(emptyResult.wordCount, 0)

        let wsResult = SelectionClassifier.classify("   \t\n  ")
        XCTAssertEqual(wsResult.state, .empty)
        XCTAssertEqual(wsResult.text, "")
        XCTAssertEqual(wsResult.wordCount, 0)
    }

    func testSingleWordClassifiesAsWordAndPreservesSurroundingWhitespace() {
        let result = SelectionClassifier.classify("  eloquent\n", isReadOnly: true)
        XCTAssertEqual(result.state, .word)
        XCTAssertEqual(result.text, "eloquent")
        XCTAssertEqual(result.wordCount, 1)
        XCTAssertTrue(result.isReadOnly)
        XCTAssertEqual(result.leadingWhitespace, "  ")
        XCTAssertEqual(result.trailingWhitespace, "\n")
        XCTAssertEqual(result.restoreSurroundingWhitespace("articulate"), "  articulate\n")
    }

    func testWordLengthBoundaryAt64Characters() {
        let exact64 = String(repeating: "a", count: 64)
        let boundaryWord = SelectionClassifier.classify(exact64)
        XCTAssertEqual(boundaryWord.state, .word)
        XCTAssertEqual(boundaryWord.wordCount, 1)

        let long65 = String(repeating: "a", count: 65)
        let longPassage = SelectionClassifier.classify(long65)
        XCTAssertEqual(longPassage.state, .passage)
        XCTAssertEqual(longPassage.wordCount, 1)
    }

    func testMultiWordTextClassifiesAsPassage() {
        let result = SelectionClassifier.classify("Their going too the store.")
        XCTAssertEqual(result.state, .passage)
        XCTAssertEqual(result.wordCount, 5)
        XCTAssertEqual(result.text, "Their going too the store.")
    }

    func testReadOnlyFlagIsPreservedAcrossStates() {
        XCTAssertTrue(SelectionClassifier.classify("", isReadOnly: true).isReadOnly)
        XCTAssertTrue(SelectionClassifier.classify("hello", isReadOnly: true).isReadOnly)
        XCTAssertTrue(SelectionClassifier.classify("hello world", isReadOnly: true).isReadOnly)
    }

    // MARK: - AppCaptureRules

    func testBuiltInDefaultsMatchKnownBundleIdentifiers() {
        let rules = AppCaptureRules(rules: AppCaptureRules.builtInDefaults)
        XCTAssertEqual(rules.strategy(for: "com.microsoft.VSCode"), .clipboard)
        XCTAssertEqual(rules.strategy(for: "com.apple.Terminal"), .clipboardNoKeys)
        XCTAssertEqual(rules.strategy(for: "com.googlecode.iterm2"), .clipboardNoKeys)
        XCTAssertEqual(rules.strategy(for: "com.apple.Preview"), .clipboardReadOnly)
        XCTAssertEqual(rules.strategy(for: "dev.warp.Warp-Stable"), .clipboardNoKeys)
        XCTAssertNil(rules.strategy(for: "com.apple.TextEdit"))
    }

    func testBundleIdentifierLookupNormalizesCaseAndWhitespace() {
        let rules = AppCaptureRules(rawRules: [
            "  COM.MICROSOFT.VSCODE  ": " CLIPBOARD ",
            "com.example.Reader": "clipboard-ro",
            "com.example.Unknown": "invalid-strategy",
        ])

        XCTAssertEqual(rules.strategy(for: "com.microsoft.vscode"), .clipboard)
        XCTAssertEqual(rules.strategy(for: "  com.Example.READER\n"), .clipboardReadOnly)
        XCTAssertNil(rules.strategy(for: "com.example.unknown"))
        XCTAssertNil(rules.strategy(for: "   "))
        XCTAssertNil(rules.strategy(for: nil))
    }

    func testUserRulesOverrideBundledRules() throws {
        let tempDir = FileManager.default.temporaryDirectory
            .appendingPathComponent("emendly-rules-test-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: tempDir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: tempDir) }

        let bundledURL = tempDir.appendingPathComponent("bundled.json")
        let userURL = tempDir.appendingPathComponent("user.json")

        try #"{"com.microsoft.VSCode":"clipboard","com.example.App":"ax"}"#
            .write(to: bundledURL, atomically: true, encoding: .utf8)
        try #"{"com.microsoft.VSCode":"clipboard-ro","com.example.Custom":"clipboard-nokeys"}"#
            .write(to: userURL, atomically: true, encoding: .utf8)

        let merged = AppCaptureRules.loadDefault(bundledURL: bundledURL, userURL: userURL)
        XCTAssertEqual(merged.strategy(for: "com.microsoft.VSCode"), .clipboardReadOnly)
        XCTAssertEqual(merged.strategy(for: "com.example.App"), .ax)
        XCTAssertEqual(merged.strategy(for: "com.example.Custom"), .clipboardNoKeys)
    }

    func testMalformedRulesFileFallsBackGracefully() throws {
        let tempURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("emendly-malformed-\(UUID().uuidString).json")
        try "{ not valid json".write(to: tempURL, atomically: true, encoding: .utf8)
        defer { try? FileManager.default.removeItem(at: tempURL) }

        let loaded = AppCaptureRules.load(from: tempURL)
        XCTAssertEqual(loaded, AppCaptureRules.empty)
    }

    // MARK: - HotkeySpec & Coordinate Conversion

    func testHotkeySpecParsing() {
        let defaultSpec = HotkeySpec.parse("Cmd+Option+E")
        XCTAssertNotNil(defaultSpec)
        XCTAssertEqual(defaultSpec?.displayString, "Cmd+Option+E")

        let ctrlAlt = HotkeySpec.parse("Ctrl+Alt+E")
        XCTAssertNotNil(ctrlAlt)
        XCTAssertEqual(ctrlAlt?.displayString, "Ctrl+Option+E")

        let fnKey = HotkeySpec.parse("F6")
        XCTAssertNotNil(fnKey)
        XCTAssertEqual(fnKey?.displayString, "F6")

        XCTAssertNil(HotkeySpec.parse("E"))
        XCTAssertNil(HotkeySpec.parse(""))
        XCTAssertNil(HotkeySpec.parse("Cmd+InvalidKey"))
    }

    func testAxRectToCocoaConversion() {
        let axRect = CGRect(x: 100, y: 200, width: 80, height: 24)
        let cocoa = AccessibilityCapture.axRectToCocoa(axRect, primaryScreenHeight: 1080)
        XCTAssertEqual(cocoa.origin.x, 100)
        XCTAssertEqual(cocoa.origin.y, 1080 - 200 - 24)
        XCTAssertEqual(cocoa.size.width, 80)
        XCTAssertEqual(cocoa.size.height, 24)
    }
}
