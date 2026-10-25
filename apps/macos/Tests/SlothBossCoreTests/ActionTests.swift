import XCTest
@testable import SlothBossCore

final class ActionTests: XCTestCase {
    private func count(_ ws: Workspace, _ table: String) throws -> Int64 {
        precondition(["tasks", "pages", "ai_actions"].contains(table))
        return try ws.read { try $0.scalarInt("SELECT count(*) FROM \(table)") }
    }

    private func task(_ title: String, priority: String = "low", pageId: String? = nil) -> ActionPayload {
        .createTask(CreateTaskPayload(title: title, description: nil, priority: priority, dueDate: nil, pageId: pageId))
    }

    func testProposalsAndRejectionsDoNotMutateTheWorkspace() throws {
        let t = try makeWorkspace()
        let page = try t.ws.pages.create("Research")
        let p = try t.ws.actions.propose(batchId: Ids.new(), task("Investigate", priority: "medium", pageId: page.id), summary: "Create task", model: "gemma")
        XCTAssertEqual(0, try count(t.ws, "tasks"))

        let rejected = try t.ws.actions.reject(p.id)
        XCTAssertEqual(ActionStatus.rejected, rejected.status)
        XCTAssertEqual(0, try count(t.ws, "tasks"))
        assertThrowsKind(.general) { try t.ws.actions.approve(p.id) }
        assertThrowsKind(.general) { try t.ws.actions.reject(p.id) }
        XCTAssertEqual(0, try count(t.ws, "tasks"))
    }

    func testApprovingCreatesLinkedTaskAndRecordsResult() throws {
        let t = try makeWorkspace()
        let page = try t.ws.pages.create("Research")
        let payload = ActionPayload.createTask(CreateTaskPayload(title: "Investigate", description: "why", priority: "high", dueDate: "2026-11-01", pageId: page.id))
        let p = try t.ws.actions.propose(batchId: Ids.new(), payload, summary: "s", model: "gemma")
        let applied = try t.ws.actions.approve(p.id)
        XCTAssertEqual(ActionStatus.applied, applied.status)
        let tasks = try t.ws.tasks.forPage(page.id)
        XCTAssertEqual(1, tasks.count)
        XCTAssertEqual("Investigate", tasks.first?.title)
        XCTAssertEqual(tasks.first?.id, applied.createdRecordId)
        XCTAssertNotNil(applied.decidedAt)
    }

    func testApprovalFailsCleanlyWhenTargetDisappeared() throws {
        let t = try makeWorkspace()
        let page = try t.ws.pages.create("Research")
        let p = try t.ws.actions.propose(batchId: Ids.new(), task("Investigate", priority: "medium", pageId: page.id), summary: "s", model: nil)
        try t.ws.pages.delete(page.id)
        let result = try t.ws.actions.approve(p.id)
        XCTAssertEqual(ActionStatus.failed, result.status)
        XCTAssertTrue(result.error?.contains("no longer exists") == true, result.error ?? "")
        XCTAssertEqual(0, try count(t.ws, "tasks"))
    }

    func testMalformedOrUnsupportedPayloadsAreRejected() {
        let cases = [
            #"{"type":"execute_sql","sql":"DROP TABLE pages"}"#,
            #"{"type":"create_task","title":"x","priority":"medium","sql":"1"}"#,
            #"{"type":"create_task","title":"","priority":"medium"}"#,
            #"{"type":"create_task","title":"x","priority":"whenever"}"#,
            #"{"type":"create_task","title":"x","priority":"low","due_date":"next tuesday"}"#,
            #"{"type":"create_task","title":"x","priority":"low","page_id":"'; DROP TABLE pages; --"}"#,
            #"{"type":"create_task","title":5,"priority":"low"}"#,
            #"{"type":"update_task","task_id":"0b0c6a52-0d6f-4a37-9b67-3f0f1c2b1a10","changes":{"title":"hijack"}}"#,
            #"{"type":"update_task","task_id":"0b0c6a52-0d6f-4a37-9b67-3f0f1c2b1a10","changes":{}}"#,
            #"{"type":"add_page_reference","page_id":"0b0c6a52-0d6f-4a37-9b67-3f0f1c2b1a10","target_page_id":"0b0c6a52-0d6f-4a37-9b67-3f0f1c2b1a10"}"#,
            #"{"type":"create_page","title":"x","content":{"version":1,"blocks":[{"type":"script","spans":[]}]}}"#,
            #"{"type":"draft_page_content","page_id":"0b0c6a52-0d6f-4a37-9b67-3f0f1c2b1a10","blocks":[]}"#,
            "[1,2,3]",
            "not json",
        ]
        for json in cases {
            assertThrowsKind(.actionValidation) { _ = try ActionPayload.parse(json) }
        }
    }

    func testPayloadsRoundTripThroughStrictParser() throws {
        let id = Ids.new()
        let payloads: [ActionPayload] = [
            .createTask(CreateTaskPayload(title: "T", description: nil, priority: "low", dueDate: "2026-01-02", pageId: id)),
            .createPage(CreatePagePayload(title: "P", parentId: nil, content: PageDocument([Block.paragraph("x")]))),
            .draftPageContent(DraftPageContentPayload(pageId: id, blocks: [Block.heading(2, "H")])),
            .addPageReference(AddPageReferencePayload(pageId: id, targetPageId: Ids.new())),
            .updateTask(UpdateTaskPayload(taskId: id, status: nil, priority: nil, changesDueDate: true, dueDate: nil)),
        ]
        for p in payloads {
            XCTAssertEqual(p, try ActionPayload.parse(p.toJSON()), p.toJSON())
        }
        XCTAssertEqual(#"{"type":"update_task","task_id":"\#(id)","changes":{"due_date":null}}"#, payloads[4].toJSON())
    }

    func testUndoCreateTaskOnlyWhenUnchanged() throws {
        useSteppingClock()
        let t = try makeWorkspace()
        let a = try t.ws.actions.approve(t.ws.actions.propose(batchId: Ids.new(), task("A"), summary: "s", model: nil).id)
        let b = try t.ws.actions.approve(t.ws.actions.propose(batchId: Ids.new(), task("B"), summary: "s", model: nil).id)

        XCTAssertEqual(ActionStatus.undone, try t.ws.actions.undo(a.id).status)
        XCTAssertNil(try t.ws.tasks.get(try XCTUnwrap(a.createdRecordId)))

        let bTask = try XCTUnwrap(b.createdRecordId)
        try t.ws.tasks.setStatus(bTask, TaskState.inProgress) // user edits after approval
        XCTAssertThrowsError(try t.ws.actions.undo(b.id)) { error in
            XCTAssertTrue(describeError(error).contains("edited"))
        }
        XCTAssertNotNil(try t.ws.tasks.get(bTask))
        XCTAssertEqual(ActionStatus.applied, try t.ws.actions.get(b.id)?.status)
    }

    func testDraftContentAndPageReferenceAppendAndUndo() throws {
        useSteppingClock()
        let t = try makeWorkspace()
        let page = try t.ws.pages.create("Notes", content: PageDocument([Block.paragraph("original")]))
        let other = try t.ws.pages.create("Other")
        let original = try XCTUnwrap(try t.ws.pages.get(page.id)?.contentJSON)

        let draft = try t.ws.actions.approve(t.ws.actions.propose(batchId: Ids.new(), .draftPageContent(DraftPageContentPayload(
            pageId: page.id, blocks: [Block.heading(2, "Summary"), Block(.bulleted, [TextSpan("point")])])), summary: "s", model: nil).id)
        XCTAssertEqual(ActionStatus.applied, draft.status)
        XCTAssertEqual("original\nSummary\npoint", try t.ws.pages.get(page.id)?.contentText)

        let link = try t.ws.actions.approve(t.ws.actions.propose(batchId: Ids.new(), .addPageReference(AddPageReferencePayload(
            pageId: page.id, targetPageId: other.id)), summary: "s", model: nil).id)
        XCTAssertEqual(ActionStatus.applied, link.status)
        XCTAssertEqual(1, try t.ws.pages.backlinks(other.id).count)

        // Undoing the older action would discard the newer one's change, so it must be refused.
        assertThrowsKind(.general) { try t.ws.actions.undo(draft.id) }

        try t.ws.actions.undo(link.id)
        XCTAssertTrue(try t.ws.pages.backlinks(other.id).isEmpty)
        try t.ws.actions.undo(draft.id)
        XCTAssertEqual(original, try t.ws.pages.get(page.id)?.contentJSON)
    }

    func testDuplicatePageReferenceFailsWithoutChange() throws {
        let t = try makeWorkspace()
        let page = try t.ws.pages.create("Notes")
        let other = try t.ws.pages.create("Other")
        let ref = ActionPayload.addPageReference(AddPageReferencePayload(pageId: page.id, targetPageId: other.id))
        try t.ws.actions.approve(t.ws.actions.propose(batchId: Ids.new(), ref, summary: "s", model: nil).id)
        let before = try t.ws.pages.get(page.id)?.contentJSON
        let second = try t.ws.actions.approve(t.ws.actions.propose(batchId: Ids.new(), ref, summary: "s", model: nil).id)
        XCTAssertEqual(ActionStatus.failed, second.status)
        XCTAssertEqual(before, try t.ws.pages.get(page.id)?.contentJSON)
    }

    func testUpdateTaskAppliesWhitelistedFieldsAndUndoes() throws {
        useSteppingClock()
        let t = try makeWorkspace()
        let created = try t.ws.tasks.create(TaskDraft("T", priority: TaskPriorityLevel.low))
        let a = try t.ws.actions.approve(t.ws.actions.propose(batchId: Ids.new(), .updateTask(UpdateTaskPayload(
            taskId: created.id, status: TaskState.done, priority: TaskPriorityLevel.high, changesDueDate: true, dueDate: "2026-12-01")),
            summary: "s", model: nil).id)
        XCTAssertEqual(ActionStatus.applied, a.status)
        let updated = try XCTUnwrap(try t.ws.tasks.get(created.id))
        XCTAssertEqual(TaskState.done, updated.status)
        XCTAssertEqual(TaskPriorityLevel.high, updated.priority)
        XCTAssertEqual("2026-12-01", updated.dueDate)
        XCTAssertNotNil(updated.completedAt)

        try t.ws.actions.undo(a.id)
        let reverted = try XCTUnwrap(try t.ws.tasks.get(created.id))
        XCTAssertEqual(TaskState.todo, reverted.status)
        XCTAssertEqual(TaskPriorityLevel.low, reverted.priority)
        XCTAssertNil(reverted.dueDate)
        XCTAssertNil(reverted.completedAt)
    }

    func testChainedActionsUndoInReverseOrderButUserEditsBlock() throws {
        useSteppingClock()
        let t = try makeWorkspace()
        let create = try t.ws.actions.approve(t.ws.actions.propose(batchId: Ids.new(), task("T"), summary: "s", model: nil).id)
        let taskId = try XCTUnwrap(create.createdRecordId)
        let update = try t.ws.actions.approve(t.ws.actions.propose(batchId: Ids.new(), .updateTask(UpdateTaskPayload(
            taskId: taskId, status: nil, priority: "high", changesDueDate: false, dueDate: nil)), summary: "s", model: nil).id)
        try t.ws.actions.undo(update.id)
        try t.ws.actions.undo(create.id)
        XCTAssertNil(try t.ws.tasks.get(taskId))

        let create2 = try t.ws.actions.approve(t.ws.actions.propose(batchId: Ids.new(), task("U"), summary: "s", model: nil).id)
        let id2 = try XCTUnwrap(create2.createdRecordId)
        let update2 = try t.ws.actions.approve(t.ws.actions.propose(batchId: Ids.new(), .updateTask(UpdateTaskPayload(
            taskId: id2, status: nil, priority: "high", changesDueDate: false, dueDate: nil)), summary: "s", model: nil).id)
        var draft = TaskStore.toDraft(try XCTUnwrap(try t.ws.tasks.get(id2)))
        draft.title = "U edited"
        try t.ws.tasks.update(id2, draft)
        assertThrowsKind(.general) { try t.ws.actions.undo(update2.id) }
        assertThrowsKind(.general) { try t.ws.actions.undo(create2.id) }
        XCTAssertEqual("U edited", try t.ws.tasks.get(id2)?.title)
    }

    func testCreatePageActionAndUndo() throws {
        let t = try makeWorkspace()
        let a = try t.ws.actions.approve(t.ws.actions.propose(batchId: Ids.new(), .createPage(CreatePagePayload(
            title: "Drafted", parentId: nil, content: PageDocument([Block.paragraph("body")]))), summary: "s", model: nil).id)
        let pageId = try XCTUnwrap(a.createdRecordId)
        XCTAssertEqual("body", try t.ws.pages.get(pageId)?.contentText)
        try t.ws.actions.undo(a.id)
        XCTAssertNil(try t.ws.pages.get(pageId))
        assertThrowsKind(.general) { try t.ws.actions.undo(a.id) }
    }

    func testCreatePageUndoRefusedWhenSubpagesExist() throws {
        let t = try makeWorkspace()
        let a = try t.ws.actions.approve(t.ws.actions.propose(batchId: Ids.new(), .createPage(CreatePagePayload(
            title: "Drafted", parentId: nil, content: PageDocument())), summary: "s", model: nil).id)
        let pageId = try XCTUnwrap(a.createdRecordId)
        try t.ws.pages.create("Child", parentId: pageId)
        assertThrowsKind(.general) { try t.ws.actions.undo(a.id) }
        XCTAssertNotNil(try t.ws.pages.get(pageId))
    }

    func testHistoryRecordsEveryDecision() throws {
        let t = try makeWorkspace()
        let batch = Ids.new()
        let p1 = try t.ws.actions.propose(batchId: batch, task("A"), summary: "s", model: nil)
        let p2 = try t.ws.actions.propose(batchId: batch, task("B"), summary: "s", model: nil)
        try t.ws.actions.approve(p1.id)
        try t.ws.actions.reject(p2.id)
        XCTAssertEqual([ActionStatus.applied, ActionStatus.rejected], try t.ws.actions.batch(batch).map(\.status))
        XCTAssertEqual(2, try t.ws.actions.history().count)
    }
}
