// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "VertexCore",
    platforms: [.macOS(.v14), .iOS(.v17)],
    products: [
        .library(name: "VertexCore", targets: ["VertexCore"]),
    ],
    targets: [
        .target(
            name: "VertexCore",
            path: "Sources/VertexCore"
        ),
        .testTarget(
            name: "VertexCoreTests",
            dependencies: ["VertexCore"]
        ),
    ]
)
