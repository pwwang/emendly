// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "EmendlyMac",
    platforms: [
        .macOS(.v13),
    ],
    products: [
        .executable(name: "EmendlyApp", targets: ["EmendlyApp"]),
    ],
    targets: [
        .target(
            name: "EmendlyCapture",
            path: "Sources/EmendlyCapture",
            linkerSettings: [
                .linkedFramework("AppKit"),
                .linkedFramework("ApplicationServices"),
                .linkedFramework("Carbon"),
            ]
        ),
        .executableTarget(
            name: "EmendlyApp",
            dependencies: ["EmendlyCapture"],
            path: "Sources/EmendlyApp",
            linkerSettings: [
                .linkedFramework("AppKit"),
                .linkedFramework("SwiftUI"),
                .linkedFramework("Security"),
            ]
        ),
        .testTarget(
            name: "EmendlyCaptureTests",
            dependencies: ["EmendlyCapture"],
            path: "Tests/EmendlyCaptureTests"
        ),
    ]
)
