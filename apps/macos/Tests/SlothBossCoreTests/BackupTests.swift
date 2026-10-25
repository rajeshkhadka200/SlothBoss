import XCTest
@testable import SlothBossCore

final class BackupTests: XCTestCase {
    func testExportThenRestorePreservesPagesTasksHierarchyAndLinks() throws {
        let source = try makeWorkspace()
        let root = try source.ws.pages.create("Root")
        let child = try source.ws.pages.create("Child", parentId: root.id, content: PageDocument([
            Block(.paragraph, [TextSpan("see "), TextSpan("Root", bold: true, link: Links.forPage(root.id))]),
            Block(.todo, [TextSpan("check")], checked: true),
        ]))
        try source.ws.pages.setFavorite(child.id, true)
        let archived = try source.ws.pages.create("Old")
        try source.ws.pages.archive(archived.id)
        let task = try source.ws.tasks.create(TaskDraft("Do it", description: "desc", status: TaskState.done, priority: TaskPriorityLevel.urgent,
                                                        dueDate: "2026-10-30", pageId: child.id))
        try source.ws.tasks.create(TaskDraft("Free"))

        let file = source.dir.appendingPathComponent("backup.json").path
        try BackupService.exportToFile(source.ws, path: file, generator: "test")
        let validated = try BackupService.validateFile(file)

        let target = try makeWorkspace()
        try target.ws.pages.create("Will be replaced")
        try BackupService.restoreReplacing(target.ws, validated)

        XCTAssertEqual(try source.ws.pages.list(includeArchived: true).sorted { $0.id < $1.id },
                       try target.ws.pages.list(includeArchived: true).sorted { $0.id < $1.id })
        XCTAssertEqual(try source.ws.pages.get(child.id)?.contentJSON, try target.ws.pages.get(child.id)?.contentJSON)
        XCTAssertEqual(try source.ws.tasks.list().sorted { $0.id < $1.id }, try target.ws.tasks.list().sorted { $0.id < $1.id })
        XCTAssertEqual([child.id], try target.ws.pages.backlinks(root.id).map(\.id))
        XCTAssertEqual(task.completedAt, try target.ws.tasks.get(task.id)?.completedAt)
        XCTAssertTrue(try target.ws.search.query("Old").isEmpty)
        XCTAssertEqual(1, try target.ws.search.query("Do it").count)
        XCTAssertTrue(try target.ws.search.query("replaced").isEmpty)
    }

    func testExportFollowsBackupSchemaShape() throws {
        let t = try makeWorkspace()
        try t.ws.pages.create("A")
        let json = try JSON.parse(BackupService.export(t.ws, generator: "SlothBoss macOS test"))
        XCTAssertEqual("slothboss-backup", json["format"]?.stringValue)
        XCTAssertEqual(1, json["formatVersion"]?.intValue)
        XCTAssertEqual(Int64(Workspace.schemaVersion), json["schemaVersion"]?.intValue)
        let page = try XCTUnwrap(json["pages"]?.arrayValue?.first)
        for key in ["id", "parentId", "title", "content", "sortOrder", "createdAt", "updatedAt", "isFavorite", "isArchived"] {
            XCTAssertNotNil(page[key], key)
        }
        XCTAssertEqual(1, page["content"]?["version"]?.intValue)
    }

    func testValidFixtureRestores() throws {
        let backup = try BackupService.validate(Fixtures.read("backup/valid-v1.json"))
        let t = try makeWorkspace()
        try BackupService.restoreReplacing(t.ws, backup)
        XCTAssertEqual(2, try t.ws.pages.list().count)
        XCTAssertEqual(["Background", "Experiments"], try t.ws.pages.breadcrumb("6f1d2c3b-4a59-4e7f-8a1b-2c3d4e5f6a7b").map(\.title))
        XCTAssertEqual(1, try t.ws.tasks.forPage("6f1d2c3b-4a59-4e7f-8a1b-2c3d4e5f6a7b").count)
        XCTAssertEqual(1, try t.ws.pages.backlinks("0b0c6a52-0d6f-4a37-9b67-3f0f1c2b1a10").count)
        XCTAssertEqual(1, try t.ws.pages.favorites().count)
        XCTAssertEqual("2026-10-20T11:00:00.000Z", try t.ws.tasks.get("9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d")?.completedAt)
        XCTAssertEqual("fixture", backup.generator)
    }

    func testInvalidBackupsAreRejectedBeforeAnyWrite() throws {
        let cases = try XCTUnwrap(Fixtures.json("backup/invalid-cases.json")["cases"]?.arrayValue)
        XCTAssertFalse(cases.isEmpty)
        let t = try makeWorkspace()
        let existing = try t.ws.pages.create("Keep me")
        for c in cases {
            let name = c["name"]?.stringValue ?? "?"
            let json = try XCTUnwrap(c["backup"]).serialized()
            XCTAssertThrowsError(try BackupService.restoreReplacing(t.ws, BackupService.validate(json)), name) { error in
                guard let e = error as? SlothBossError else { return XCTFail("\(name): unexpected \(error)") }
                XCTAssertEqual(.backupValidation, e.kind, name)
                XCTAssertFalse(e.message.isBlank, name)
            }
            XCTAssertNotNil(try t.ws.pages.get(existing.id), name)
        }
    }

    func testNotJSONAndWrongRootAreRejected() {
        for json in ["", "[]", "{\"format\":5}", "{\"format\":\"slothboss-backup\",\"formatVersion\":\"1\"}"] {
            assertThrowsKind(.backupValidation) { _ = try BackupService.validate(json) }
        }
    }

    func testCompletionTimesAreRepairedWithWarnings() throws {
        let json = """
            {"format":"slothboss-backup","formatVersion":1,"schemaVersion":1,"exportedAt":"","pages":[],"tasks":[
              {"id":"33333333-3333-4333-8333-333333333333","title":"Done","status":"done","priority":"low",
               "createdAt":"2026-10-20T10:00:00.000Z","updatedAt":"2026-10-20T11:00:00.000Z"},
              {"id":"44444444-4444-4444-8444-444444444444","title":"Open","status":"todo","priority":"low",
               "createdAt":"2026-10-20T10:00:00.000Z","updatedAt":"2026-10-20T11:00:00.000Z","completedAt":"2026-10-20T11:00:00.000Z"}
            ]}
            """
        let backup = try BackupService.validate(json)
        XCTAssertEqual(2, backup.warnings.count)
        XCTAssertEqual("2026-10-20T11:00:00.000Z", backup.tasks[0].completedAt)
        XCTAssertNil(backup.tasks[1].completedAt)
    }

    func testExportFileNamesAreSanitized() {
        let cases: [(String, String)] = [
            ("../../evil", "-..-evil.md"),
            ("C:\\Windows\\x", "C--Windows-x.md"),
            ("   ", "Untitled.md"),
            ("Plan: v2?", "Plan- v2-.md"),
        ]
        for (title, expected) in cases {
            let name = BackupService.safeFileName(title, extension: ".md")
            XCTAssertEqual(expected, name, title)
            XCTAssertFalse(name.contains("/"))
        }
    }
}

/// The bundled copies of /contracts must stay byte-identical to the shared originals.
final class ContractSyncTests: XCTestCase {
    private let synced = ["schema", "schemas", "prompts"]

    /// Relative paths of regular files under `dir` (hidden files such as .DS_Store ignored).
    private func files(in dir: URL) throws -> [String] {
        let fm = FileManager.default
        var result: [String] = []
        for sub in try fm.subpathsOfDirectory(atPath: dir.path) {
            if (sub as NSString).lastPathComponent.hasPrefix(".") { continue }
            var isDir: ObjCBool = false
            if fm.fileExists(atPath: dir.appendingPathComponent(sub).path, isDirectory: &isDir), !isDir.boolValue {
                result.append(sub)
            }
        }
        return result.sorted()
    }

    func testCommittedCopiesByteMatchContracts() throws {
        // Source tree copy, located via #filePath: apps/macos/Sources/SlothBossCore/Resources/contracts
        let copyRoot = Fixtures.root.appendingPathComponent("apps/macos/Sources/SlothBossCore/Resources/contracts")
        for dir in synced {
            let original = Fixtures.contracts.appendingPathComponent(dir)
            let copy = copyRoot.appendingPathComponent(dir)
            let originalFiles = try files(in: original)
            XCTAssertFalse(originalFiles.isEmpty, dir)
            XCTAssertEqual(originalFiles, try files(in: copy), "File lists differ in \(dir); run apps/macos/scripts/sync-contracts.sh")
            for name in originalFiles {
                let a = try Data(contentsOf: original.appendingPathComponent(name))
                let b = try Data(contentsOf: copy.appendingPathComponent(name))
                XCTAssertEqual(a, b, "\(dir)/\(name) differs from contracts/; run apps/macos/scripts/sync-contracts.sh")
            }
        }
    }

    func testBundledResourcesByteMatchContracts() throws {
        let bundled = try XCTUnwrap(ContractResources.directory, "resource bundle not found")
        for dir in synced {
            let original = Fixtures.contracts.appendingPathComponent(dir)
            for name in try files(in: original) {
                let a = try Data(contentsOf: original.appendingPathComponent(name))
                let b = try Data(contentsOf: bundled.appendingPathComponent(dir).appendingPathComponent(name))
                XCTAssertEqual(a, b, "bundled \(dir)/\(name) differs from contracts/")
            }
        }
    }
}
