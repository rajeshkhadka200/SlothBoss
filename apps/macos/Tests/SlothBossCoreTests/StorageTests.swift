import XCTest
@testable import SlothBossCore

final class StorageTests: XCTestCase {
    func testMigrationSetsSchemaVersionAndReopenIsIdempotent() throws {
        let t = try makeWorkspace()
        let page = try t.ws.pages.create("Persist me", content: PageDocument([Block.paragraph("hello")]))
        XCTAssertEqual(Int64(Workspace.schemaVersion), try t.ws.read { try $0.scalarInt("PRAGMA user_version") })
        XCTAssertEqual(1, try t.ws.read { try $0.scalarInt("PRAGMA foreign_keys") })
        XCTAssertEqual("wal", try t.ws.read { try $0.scalarString("PRAGMA journal_mode") })

        let reopened = try t.reopen()
        let loaded = try XCTUnwrap(try reopened.pages.get(page.id))
        XCTAssertEqual("Persist me", loaded.title)
        XCTAssertEqual("hello", try loaded.document().plainText())
    }

    func testNewerSchemaVersionIsRefusedWithoutModification() throws {
        let t = try makeWorkspace()
        _ = try t.ws.read { try $0.run("PRAGMA user_version = 99") }
        t.ws.close()
        let before = try Data(contentsOf: URL(fileURLWithPath: t.dbPath))
        XCTAssertThrowsError(try Workspace.open(path: t.dbPath)) { error in
            XCTAssertTrue(describeError(error).contains("newer"), describeError(error))
        }
        XCTAssertEqual(before, try Data(contentsOf: URL(fileURLWithPath: t.dbPath)))
    }

    func testMigrationSQLIsTheSharedContract() throws {
        let bundled = try ContractResources.read("schema/001_initial.sql")
        let shared = String(decoding: try Data(contentsOf: Fixtures.contracts.appendingPathComponent("schema/001_initial.sql")), as: UTF8.self)
        XCTAssertEqual(shared, bundled)
    }

    func testNestedPagesAndBreadcrumbs() throws {
        let t = try makeWorkspace()
        let root = try t.ws.pages.create("Root")
        let child = try t.ws.pages.create("Child", parentId: root.id)
        let grandchild = try t.ws.pages.create("Grandchild", parentId: child.id)
        XCTAssertEqual(["Root", "Child", "Grandchild"], try t.ws.pages.breadcrumb(grandchild.id).map(\.title))
        XCTAssertEqual(1, try t.ws.pages.children(root.id).count)
    }

    func testCyclesArePrevented() throws {
        let t = try makeWorkspace()
        let a = try t.ws.pages.create("A")
        let b = try t.ws.pages.create("B", parentId: a.id)
        let c = try t.ws.pages.create("C", parentId: b.id)
        assertThrowsKind(.general) { try t.ws.pages.move(a.id, to: c.id) }
        assertThrowsKind(.general) { try t.ws.pages.move(a.id, to: a.id) }
        XCTAssertNil(try t.ws.pages.get(a.id)?.parentId)
        try t.ws.pages.move(c.id, to: nil)
        XCTAssertNil(try t.ws.pages.get(c.id)?.parentId)
    }

    func testCreatingUnderMissingParentFails() throws {
        let t = try makeWorkspace()
        assertThrowsKind(.general) { try t.ws.pages.create("x", parentId: Ids.new()) }
    }

    func testDeletingPageRemovesSubtreeAndUnlinksTasks() throws {
        let t = try makeWorkspace()
        let root = try t.ws.pages.create("Root")
        let child = try t.ws.pages.create("Child", parentId: root.id)
        let task = try t.ws.tasks.create(TaskDraft("Linked", pageId: child.id))
        let impact = try t.ws.pages.deletionImpact(root.id)
        XCTAssertEqual(2, impact.pages)
        XCTAssertEqual(1, impact.linkedTasks)

        try t.ws.pages.delete(root.id)

        XCTAssertNil(try t.ws.pages.get(child.id))
        let kept = try XCTUnwrap(try t.ws.tasks.get(task.id))
        XCTAssertNil(kept.pageId)
        XCTAssertTrue(try t.ws.search.query("Child").isEmpty)
    }

    func testArchiveHidesSubtreeAndRestoreMovesToTopLevelWhenParentArchived() throws {
        let t = try makeWorkspace()
        let root = try t.ws.pages.create("Root")
        let child = try t.ws.pages.create("Archivable child", parentId: root.id)
        try t.ws.pages.archive(root.id)
        XCTAssertFalse(try t.ws.pages.list().contains { $0.id == child.id })
        XCTAssertTrue(try t.ws.search.query("Archivable").isEmpty)
        XCTAssertEqual([root.id], try t.ws.pages.archived().map(\.id))

        try t.ws.pages.restore(child.id)
        let restored = try XCTUnwrap(try t.ws.pages.get(child.id))
        XCTAssertFalse(restored.isArchived)
        XCTAssertNil(restored.parentId)
        XCTAssertEqual(1, try t.ws.search.query("Archivable").count)
    }

    func testRestoringRootRestoresWholeSubtree() throws {
        let t = try makeWorkspace()
        let root = try t.ws.pages.create("Root")
        let child = try t.ws.pages.create("Child", parentId: root.id)
        try t.ws.pages.archive(root.id)
        try t.ws.pages.restore(root.id)
        XCTAssertEqual(root.id, try t.ws.pages.get(child.id)?.parentId)
        XCTAssertEqual(2, try t.ws.pages.list().count)
        XCTAssertTrue(try t.ws.pages.archived().isEmpty)
    }

    func testContentSaveUpdatesLinksBacklinksAndSearch() throws {
        let t = try makeWorkspace()
        let target = try t.ws.pages.create("Target")
        let source = try t.ws.pages.create("Source")
        try t.ws.pages.updateContent(source.id, PageDocument([
            Block(.paragraph, [TextSpan("see "), TextSpan("Target", link: Links.forPage(target.id)), TextSpan(" about zeppelins")]),
        ]))
        XCTAssertEqual([source.id], try t.ws.pages.backlinks(target.id).map(\.id))
        XCTAssertEqual([target.id], try t.ws.pages.outgoingLinks(source.id).map(\.id))
        XCTAssertEqual(1, try t.ws.search.query("zeppelin").count)

        try t.ws.pages.updateContent(source.id, PageDocument([Block.paragraph("now about airships")]))
        XCTAssertTrue(try t.ws.pages.backlinks(target.id).isEmpty)
        XCTAssertTrue(try t.ws.search.query("zeppelin").isEmpty)
        XCTAssertEqual(1, try t.ws.search.query("airsh").count)
    }

    func testSelfLinksAndLinksToMissingPagesAreNotIndexed() throws {
        let t = try makeWorkspace()
        let page = try t.ws.pages.create("Self")
        try t.ws.pages.updateContent(page.id, PageDocument([
            Block(.paragraph, [TextSpan("me", link: Links.forPage(page.id)), TextSpan(" ghost", link: Links.forPage(Ids.new()))]),
        ]))
        XCTAssertTrue(try t.ws.pages.outgoingLinks(page.id).isEmpty)
        XCTAssertEqual(2, try t.ws.pages.get(page.id)?.document().blocks[0].spans.count)
    }

    func testFavoritesAndRename() throws {
        let t = try makeWorkspace()
        let p = try t.ws.pages.create("Old")
        try t.ws.pages.rename(p.id, "  New\nname ")
        try t.ws.pages.setFavorite(p.id, true)
        XCTAssertEqual("New name", try t.ws.pages.get(p.id)?.title)
        XCTAssertEqual(1, try t.ws.pages.favorites().count)
        XCTAssertEqual(1, try t.ws.search.query("new name").count)
    }

    func testTaskValidationAndCompletionLifecycle() throws {
        let t = try makeWorkspace()
        assertThrowsKind(.general) { try t.ws.tasks.create(TaskDraft("   ")) }
        assertThrowsKind(.general) { try t.ws.tasks.create(TaskDraft("x", priority: "critical")) }
        assertThrowsKind(.general) { try t.ws.tasks.create(TaskDraft("x", dueDate: "2026-13-01")) }
        assertThrowsKind(.general) { try t.ws.tasks.create(TaskDraft("x", dueDate: "2026-02-30")) }
        assertThrowsKind(.general) { try t.ws.tasks.create(TaskDraft("x", pageId: Ids.new())) }

        let task = try t.ws.tasks.create(TaskDraft("Write report", dueDate: "2026-10-25"))
        XCTAssertNil(task.completedAt)
        let done = try t.ws.tasks.setStatus(task.id, TaskState.done)
        XCTAssertNotNil(done.completedAt)
        let reopened = try t.ws.tasks.setStatus(task.id, TaskState.todo)
        XCTAssertNil(reopened.completedAt)
    }

    func testCompletedAtIsKeptWhileStayingDone() throws {
        useSteppingClock()
        let t = try makeWorkspace()
        let task = try t.ws.tasks.create(TaskDraft("Done already", status: TaskState.done))
        let first = try XCTUnwrap(task.completedAt)
        var draft = TaskStore.toDraft(task)
        draft.title = "Renamed"
        XCTAssertEqual(first, try t.ws.tasks.update(task.id, draft).completedAt)
    }

    func testTaskFilters() throws {
        let t = try makeWorkspace()
        let page = try t.ws.pages.create("P")
        let today = SBClock.today()
        let yesterday = try XCTUnwrap(SBClock.addDays(today, -1))
        let nextWeek = try XCTUnwrap(SBClock.addDays(today, 7))
        try t.ws.tasks.create(TaskDraft("overdue", priority: TaskPriorityLevel.high, dueDate: yesterday))
        try t.ws.tasks.create(TaskDraft("today", dueDate: today, pageId: page.id))
        try t.ws.tasks.create(TaskDraft("nodate", status: TaskState.done))
        try t.ws.tasks.create(TaskDraft("week", dueDate: nextWeek))

        XCTAssertEqual(["overdue"], try t.ws.tasks.list(TaskFilter(due: .overdue)).map(\.title))
        XCTAssertEqual(["today"], try t.ws.tasks.list(TaskFilter(due: .today)).map(\.title))
        XCTAssertEqual(["today", "week"], try t.ws.tasks.list(TaskFilter(due: .next7Days)).map(\.title))
        XCTAssertEqual(["nodate"], try t.ws.tasks.list(TaskFilter(due: .noDate)).map(\.title))
        XCTAssertEqual(["overdue"], try t.ws.tasks.list(TaskFilter(priority: TaskPriorityLevel.high)).map(\.title))
        XCTAssertEqual(["today"], try t.ws.tasks.forPage(page.id).map(\.title))
        XCTAssertEqual(3, try t.ws.tasks.list(TaskFilter(hideDone: true)).count)
        XCTAssertEqual(["nodate"], try t.ws.tasks.list(TaskFilter(status: TaskState.done)).map(\.title))
    }

    func testSearchCoversTasksAndHandlesHostileOrEmptyQueries() throws {
        let t = try makeWorkspace()
        let task = try t.ws.tasks.create(TaskDraft("Calibrate telescope", description: "Use the quasar reference"))
        XCTAssertEqual(1, try t.ws.search.query("quasar").count)
        XCTAssertTrue(try t.ws.search.query("").isEmpty)
        XCTAssertTrue(try t.ws.search.query("   \"*() ").isEmpty)
        for hostile in ["\" OR 1=1 -- NEAR( title:*", "'; DROP TABLE pages; --", "title:quasar", "quasar*\"", "NOT AND OR", "^\"\"\"", "{ } [ ]"] {
            XCTAssertNoThrow(try t.ws.search.query(hostile), hostile)
        }
        XCTAssertNotNil(try t.ws.pages.list())
        XCTAssertEqual(1, try t.ws.read { try $0.scalarInt("SELECT count(*) FROM tasks") })

        var draft = TaskStore.toDraft(task)
        draft.description = "Use a pulsar instead"
        try t.ws.tasks.update(task.id, draft)
        XCTAssertTrue(try t.ws.search.query("quasar").isEmpty)
        try t.ws.tasks.delete(task.id)
        XCTAssertTrue(try t.ws.search.query("telescope").isEmpty)
    }

    func testMatchExpressionsAreQuoted() {
        XCTAssertEqual("\"a\"* \"b\"\"c\"*", SearchService.buildMatch(["a", "b\"c"], any: false))
        XCTAssertEqual("\"x\"* OR \"y\"*", SearchService.buildMatch(["x", "y"], any: true))
        XCTAssertNil(SearchService.buildMatch([], any: true))
        XCTAssertEqual(["hello", "wörld", "42"], SearchService.tokenize("Hello, WÖRLD! hello 42"))
    }

    func testSearchSnippetsMarkMatchesAndTitlesRankHigher() throws {
        let t = try makeWorkspace()
        try t.ws.pages.create("Mentions", content: PageDocument([Block.paragraph("a long paragraph that mentions gemma once")]))
        try t.ws.pages.create("Gemma notes", content: PageDocument([Block.paragraph("unrelated")]))
        let hits = try t.ws.search.query("gemma")
        XCTAssertEqual("Gemma notes", hits.first?.title)
        XCTAssertTrue(hits[1].snippet.contains(SearchHit.matchStart))
        XCTAssertTrue(hits[1].snippet.contains(SearchHit.matchEnd))
    }

    func testSettingsRoundTrip() throws {
        let t = try makeWorkspace()
        try t.ws.settings.set(SettingsStore.selectedModel, "gemma4:e2b-it-qat")
        XCTAssertEqual("gemma4:e2b-it-qat", try t.ws.settings.get(SettingsStore.selectedModel))
        try t.ws.settings.set(SettingsStore.selectedModel, nil)
        XCTAssertNil(try t.ws.settings.get(SettingsStore.selectedModel))
    }

    func testForeignKeysAndChecksAreEnforcedAtTheDatabaseLevel() throws {
        let t = try makeWorkspace()
        XCTAssertThrowsError(try t.ws.read { try $0.run(
            "INSERT INTO tasks (id, title, created_at, updated_at, page_id) VALUES ('a', 'b', 'c', 'd', 'missing')") }) { error in
            XCTAssertTrue(error is SQLiteError)
        }
        XCTAssertThrowsError(try t.ws.read { try $0.run(
            "INSERT INTO tasks (id, title, status, created_at, updated_at) VALUES ('a', 'b', 'done', 'c', 'd')") }) { error in
            XCTAssertTrue(error is SQLiteError)
        }
    }

    func testParametersAreBoundNotInterpolated() throws {
        let t = try makeWorkspace()
        let evil = "x'); DROP TABLE pages; --"
        let page = try t.ws.pages.create(evil)
        XCTAssertEqual(evil, try t.ws.pages.get(page.id)?.title)
        XCTAssertThrowsError(try t.ws.read { try $0.run("SELECT $missing") })
        XCTAssertThrowsError(try t.ws.read { try $0.run("SELECT 1", ["$unused": 1]) })
    }

    func testFailedWriteRollsBack() throws {
        let t = try makeWorkspace()
        XCTAssertThrowsError(try t.ws.write(.pages) { db in
            try t.ws.pages.insert(db, id: Ids.new(), title: "Half", parentId: nil, content: nil)
            throw SlothBossError("boom")
        })
        XCTAssertTrue(try t.ws.pages.list().isEmpty)
    }

    func testSnapshotRefusesToOverwrite() throws {
        let t = try makeWorkspace()
        let snap = t.dir.appendingPathComponent("snap.db").path
        try t.ws.snapshot(to: snap)
        XCTAssertTrue(FileManager.default.fileExists(atPath: snap))
        assertThrowsKind(.general) { try t.ws.snapshot(to: snap) }
    }

    func testDateHelpers() {
        XCTAssertTrue(SBClock.isValidDate("2024-02-29"))
        XCTAssertFalse(SBClock.isValidDate("2026-02-29"))
        XCTAssertFalse(SBClock.isValidDate("2026-1-01"))
        XCTAssertEqual("2026-03-01", SBClock.addDays("2026-02-28", 1))
        XCTAssertTrue(SBClock.isValidTimestamp("2026-10-20T10:00:00.000Z"))
        XCTAssertFalse(SBClock.isValidTimestamp("yesterday"))
        XCTAssertEqual("2026-10-20T09:00:01.000Z", SBClock.format(ISO8601DateFormatter().date(from: "2026-10-20T09:00:01Z")!))
        XCTAssertTrue(Ids.isValid(Ids.new()))
        XCTAssertFalse(Ids.isValid("0B0C6A52-0D6F-4A37-9B67-3F0F1C2B1A10"))
        XCTAssertFalse(Ids.isValid("../../etc/passwd"))
    }
}
