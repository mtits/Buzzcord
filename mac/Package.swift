// swift-tools-version:5.10
// Buzzcord for macOS: a native AppKit/SwiftUI port of the Windows app in ../src.
// Build the .app with ./build.sh (no Xcode project needed).
import PackageDescription

let package = Package(
    name: "Buzzcord",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(name: "Buzzcord", path: "Sources/Buzzcord"),
        .testTarget(name: "BuzzcordTests", dependencies: ["Buzzcord"], path: "Tests/BuzzcordTests")
    ]
)
