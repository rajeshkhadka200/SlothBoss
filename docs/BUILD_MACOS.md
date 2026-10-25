# Building the macOS client

> **Status: unverified.** The macOS client was written on a Windows machine with no Swift toolchain. It has
> never been compiled or run. `.github/workflows/macos.yml` builds and tests it on a GitHub macOS runner;
> treat it as work in progress until that job is green and someone has used the app on a Mac.

## Prerequisites
- macOS 14 Sonoma or later; Xcode 15.3+ (Swift 5.10+). No third-party packages — it uses the system
  SQLite3 (FTS5 enabled on macOS), Foundation, SwiftUI and AppKit.
- Optional: Ollama + a Gemma model ([LOCAL_AI_SETUP.md](LOCAL_AI_SETUP.md)).

## Build, test, run
```sh
cd apps/macos
./scripts/sync-contracts.sh     # copies ../../contracts into the package resources (also committed)
swift build
swift test                      # runs the shared contract fixtures plus core tests
swift run SlothBoss             # development run
./scripts/build-app.sh          # Release .app (universal if possible), ad-hoc signed, zipped to build/
```
SwiftPM can't reference files outside the package, so the contracts are copied into
`Sources/SlothBossCore/Resources/contracts`. `ContractSyncTests` fails if a copy differs from
`contracts/`, and CI fails if the sync script produces a diff.

## Layout
- `Sources/SlothBossCore` — storage (SQLite wrapper with bound parameters only), documents/Markdown,
  search, actions, backup, Ollama client (loopback only, proxies disabled), retriever, assistant.
- `Sources/SlothBoss` — SwiftUI app: `NavigationSplitView` sidebar with the page tree, an `NSTextView`
  editor bridged through `NSViewRepresentable`, tasks `Table`, search, assistant pane, Settings scene, menu
  commands (⌘N new page, ⇧⌘N subpage, ⇧⌘F search, ⌘J assistant, backup export/restore).
- `Tests/SlothBossCoreTests` — mirrors the Windows core tests; a `URLProtocol` stub streams NDJSON like
  Ollama.

## Workspace location
`~/Library/Application Support/SlothBoss/workspace.db`.

## Known differences from the Windows client (by design or not yet aligned)
- Type names: `SBClock`, `TaskPriorityLevel`, `TextSpan` (avoid clashes with Swift standard names).
- String limits are counted in Swift `Character`s, not UTF-16 units (same results for ordinary text).
- Editor: bold inside headings is not preserved; adjacent code blocks merge; an empty final block may
  lose its type. List markers and Return handling are implemented manually on TextKit 1.

## Signing and notarization
`build-app.sh` only ad-hoc signs (`codesign --sign -`). The app is **not** Developer ID-signed or
notarized, so Gatekeeper will block it on other Macs until the user right-clicks → Open (or removes the
quarantine attribute). Proper distribution needs an Apple Developer account.

## Likely first CI failures (from the author's own review)
Main-actor isolation warnings around `NSViewRepresentable`, `withThrowingTaskGroup` `Sendable`
constraints in the Ollama timeout helper, `URLSession.bytes(for:)` streaming through the test
`URLProtocol`, type-checker slowness on long SwiftUI modifier chains, and resource-bundle layout in
universal builds. Start debugging there.
