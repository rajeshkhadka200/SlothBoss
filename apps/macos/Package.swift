// swift-tools-version: 5.10
import PackageDescription

// SlothBoss for macOS. No third-party dependencies: Foundation, SwiftUI/AppKit and the
// system SQLite3 library only.
//
// The migration SQL, prompts and model-output schemas live in /contracts at the repository
// root. SwiftPM cannot reference files outside the package, so scripts/sync-contracts.sh
// copies them into Sources/SlothBossCore/Resources/contracts (the copies are committed, CI
// checks they are unchanged, and ContractSyncTests asserts they byte-match /contracts).
let package = Package(
    name: "SlothBoss",
    platforms: [.macOS(.v14)],
    products: [
        .executable(name: "SlothBoss", targets: ["SlothBoss"]),
        .library(name: "SlothBossCore", targets: ["SlothBossCore"]),
    ],
    targets: [
        .target(
            name: "SlothBossCore",
            resources: [.copy("Resources/contracts")],
            linkerSettings: [.linkedLibrary("sqlite3")]
        ),
        .executableTarget(
            name: "SlothBoss",
            dependencies: ["SlothBossCore"]
        ),
        .testTarget(
            name: "SlothBossCoreTests",
            dependencies: ["SlothBossCore"]
        ),
    ]
)
