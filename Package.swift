// swift-tools-version: 6.2
import PackageDescription

let package = Package(
    name: "TalkToText",
    platforms: [.macOS(.v26)],
    dependencies: [
        // Parakeet TDT as CoreML on the Neural Engine. Optional at runtime — Apple's
        // SpeechTranscriber remains the default and needs no dependency at all.
        .package(url: "https://github.com/FluidInference/FluidAudio.git", from: "0.15.6")
    ],
    targets: [
        // The dictionary is its own target so it can be tested directly, and because its
        // behaviour is a cross-platform contract: the Windows app reimplements this logic in
        // C#, and both sides run the same vectors in shared/dictionary-test-vectors.json.
        .target(
            name: "TalkToTextDictionary",
            path: "Sources/TalkToTextDictionary",
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
        .executableTarget(
            name: "TalkToText",
            dependencies: [
                "TalkToTextDictionary",
                .product(name: "FluidAudio", package: "FluidAudio"),
            ],
            path: "Sources/TalkToText",
            swiftSettings: [
                .swiftLanguageMode(.v6)
            ]
        ),
        .testTarget(
            name: "TalkToTextDictionaryTests",
            dependencies: ["TalkToTextDictionary"],
            path: "Tests/TalkToTextDictionaryTests",
            resources: [.copy("dictionary-test-vectors.json")],
            swiftSettings: [.swiftLanguageMode(.v6)]
        ),
    ]
)
