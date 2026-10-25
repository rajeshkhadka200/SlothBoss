import Foundation

public enum ActionStatus {
    public static let proposed = "proposed", rejected = "rejected", applied = "applied", failed = "failed", undone = "undone"
}

public struct ActionRecord: Equatable, Hashable, Identifiable, Sendable {
    public let id: String
    public let batchId: String
    public let actionType: String
    public let payloadJSON: String
    public let summary: String
    public let status: String
    public let resultJSON: String?
    public let undoJSON: String?
    public let error: String?
    public let model: String?
    public let createdAt: String
    public let decidedAt: String?
    public let undoneAt: String?

    public func payload() throws -> ActionPayload { try ActionPayload.parse(payloadJSON) }
    public var isPending: Bool { status == ActionStatus.proposed }
    public var canUndo: Bool { status == ActionStatus.applied }

    /// Id of the record created by an applied create_task/create_page action.
    public var createdRecordId: String? {
        guard let resultJSON, let result = try? JSON.parse(resultJSON) else { return nil }
        return result["task_id"]?.stringValue ?? result["page_id"]?.stringValue
    }
}

/// The only path by which AI output can change the workspace. Proposals are stored first;
/// nothing is mutated until `approve` applies the validated payload in a transaction.
public final class ActionStore: @unchecked Sendable {
    private unowned let ws: Workspace
    init(_ ws: Workspace) { self.ws = ws }

    static let columns =
        "id, batch_id, action_type, payload_json, summary, status, result_json, undo_json, error, model, created_at, decided_at, undone_at"

    static func map(_ r: SQLRow) -> ActionRecord {
        ActionRecord(id: r.string(0), batchId: r.string(1), actionType: r.string(2), payloadJSON: r.string(3), summary: r.string(4),
                     status: r.string(5), resultJSON: r.optionalString(6), undoJSON: r.optionalString(7), error: r.optionalString(8),
                     model: r.optionalString(9), createdAt: r.string(10), decidedAt: r.optionalString(11), undoneAt: r.optionalString(12))
    }

    @discardableResult
    public func propose(batchId: String, _ payload: ActionPayload, summary: String, model: String?) throws -> ActionRecord {
        try payload.validate()
        let json = payload.toJSON()
        _ = try ActionPayload.parse(json) // the stored form must round-trip through the strict parser
        let id = Ids.new()
        try ws.write(.actions) { db in
            _ = try db.run("""
                INSERT INTO ai_actions (id, batch_id, action_type, payload_json, summary, status, model, created_at)
                VALUES ($id, $batch, $type, $payload, $summary, 'proposed', $model, $now)
                """,
                ["$id": id, "$batch": batchId, "$type": payload.type, "$payload": json, "$summary": summary,
                 "$model": model, "$now": SBClock.stamp()])
        }
        return try require(id)
    }

    public func get(_ id: String) throws -> ActionRecord? {
        try ws.read { db in try db.query("SELECT \(ActionStore.columns) FROM ai_actions WHERE id = $id", ["$id": id], ActionStore.map).first }
    }

    private func require(_ id: String) throws -> ActionRecord {
        guard let r = try get(id) else { throw SlothBossError("The action no longer exists.") }
        return r
    }

    public func history(limit: Int = 200) throws -> [ActionRecord] {
        try ws.read { db in
            try db.query("SELECT \(ActionStore.columns) FROM ai_actions ORDER BY created_at DESC, rowid DESC LIMIT $n", ["$n": limit], ActionStore.map)
        }
    }

    public func batch(_ batchId: String) throws -> [ActionRecord] {
        try ws.read { db in
            try db.query("SELECT \(ActionStore.columns) FROM ai_actions WHERE batch_id = $b ORDER BY rowid", ["$b": batchId], ActionStore.map)
        }
    }

    @discardableResult
    public func reject(_ id: String) throws -> ActionRecord {
        try ws.write(.actions) { db in
            if try db.run("UPDATE ai_actions SET status = 'rejected', decided_at = $now WHERE id = $id AND status = 'proposed'",
                          ["$now": SBClock.stamp(), "$id": id]) == 0 {
                throw SlothBossError("This proposal has already been decided.")
            }
        }
        return try require(id)
    }

    /// Re-validates and applies a proposal in one transaction. If anything fails, the workspace is
    /// left untouched and the proposal is marked failed with the reason.
    @discardableResult
    public func approve(_ id: String) throws -> ActionRecord {
        guard let record = try get(id) else { throw SlothBossError("The proposal no longer exists.") }
        if !record.isPending { throw SlothBossError("This proposal has already been decided.") }
        do {
            try ws.write(.all) { db in
                let payload = try ActionPayload.parse(record.payloadJSON)
                let (result, undo) = try apply(db, payload)
                _ = try db.run(
                    "UPDATE ai_actions SET status = 'applied', decided_at = $now, result_json = $r, undo_json = $u, error = NULL WHERE id = $id",
                    ["$now": SBClock.stamp(), "$r": JSON.object(result).serialized(), "$u": undo.map { JSON.object($0).serialized() }, "$id": id])
            }
        } catch {
            guard error is SlothBossError || error is SQLiteError else { throw error }
            let message = describeError(error)
            try ws.write(.actions) { db in
                _ = try db.run("UPDATE ai_actions SET status = 'failed', decided_at = $now, error = $e WHERE id = $id",
                               ["$now": SBClock.stamp(), "$e": message, "$id": id])
            }
        }
        return try require(id)
    }

    private func apply(_ db: SQLiteDatabase, _ payload: ActionPayload) throws -> (JSONObject, JSONObject?) {
        switch payload {
        case .createTask(let p):
            let taskId = Ids.new()
            try ws.tasks.insert(db, id: taskId, draft: p.toDraft())
            return (JSONObject([("task_id", .string(taskId)), ("updated_at", .string(try updatedAt(db, "tasks", taskId)))]), nil)

        case .createPage(let p):
            let pageId = Ids.new()
            try ws.pages.insert(db, id: pageId, title: p.title, parentId: p.parentId, content: p.content)
            return (JSONObject([("page_id", .string(pageId)), ("updated_at", .string(try updatedAt(db, "pages", pageId)))]), nil)

        case .draftPageContent(let p):
            let previous = try contentJSON(db, p.pageId)
            let previousUpdated = try updatedAt(db, "pages", p.pageId)
            var doc = try PageDocument.parse(previous)
            doc.blocks.append(contentsOf: p.blocks)
            let updated = try ws.pages.updateContent(db, id: p.pageId, doc: doc)
            return (JSONObject([("page_id", .string(p.pageId)), ("updated_at", .string(updated))]),
                    JSONObject([("previous_content_json", .string(previous)), ("previous_updated_at", .string(previousUpdated))]))

        case .addPageReference(let p):
            let previous = try contentJSON(db, p.pageId)
            let previousUpdated = try updatedAt(db, "pages", p.pageId)
            guard let targetTitle = try db.scalarString("SELECT title FROM pages WHERE id = $id", ["$id": p.targetPageId]) else {
                throw SlothBossError("The referenced page no longer exists.")
            }
            var doc = try PageDocument.parse(previous)
            if doc.pageReferences().contains(p.targetPageId) {
                throw SlothBossError("The page already links to that page.")
            }
            doc.blocks.append(Block(.paragraph, [
                TextSpan("Related: "),
                TextSpan(targetTitle.isBlank ? "Untitled" : targetTitle, link: Links.forPage(p.targetPageId)),
            ]))
            let updated = try ws.pages.updateContent(db, id: p.pageId, doc: doc)
            return (JSONObject([("page_id", .string(p.pageId)), ("updated_at", .string(updated))]),
                    JSONObject([("previous_content_json", .string(previous)), ("previous_updated_at", .string(previousUpdated))]))

        case .updateTask(let p):
            guard let task = try TaskStore.get(db, p.taskId) else { throw SlothBossError("The task no longer exists.") }
            var changed = TaskStore.toDraft(task)
            changed.status = p.status ?? changed.status
            changed.priority = p.priority ?? changed.priority
            if p.changesDueDate { changed.dueDate = p.dueDate }
            let updated = try ws.tasks.update(db, id: p.taskId, draft: changed)
            return (JSONObject([("task_id", .string(p.taskId)), ("updated_at", .string(updated))]),
                    JSONObject([("status", .string(task.status)), ("priority", .string(task.priority)),
                                ("due_date", .optional(task.dueDate)), ("previous_updated_at", .string(task.updatedAt))]))
        }
    }

    /// `table` is always one of the literal names "tasks"/"pages" chosen by this file, never user input.
    private func updatedAt(_ db: SQLiteDatabase, _ table: String, _ id: String) throws -> String {
        precondition(table == "tasks" || table == "pages")
        guard let s = try db.scalarString("SELECT updated_at FROM \(table) WHERE id = $id", ["$id": id]) else {
            throw SlothBossError("The record no longer exists.")
        }
        return s
    }

    private func contentJSON(_ db: SQLiteDatabase, _ pageId: String) throws -> String {
        guard let s = try db.scalarString("SELECT content_json FROM pages WHERE id = $id", ["$id": pageId]) else {
            throw SlothBossError("The target page no longer exists.")
        }
        return s
    }

    /// Reverses an applied action, but only if the affected record is exactly as the action left it.
    /// Never overwrites edits the user made afterwards; throws with an explanation instead.
    @discardableResult
    public func undo(_ id: String) throws -> ActionRecord {
        guard let record = try get(id) else { throw SlothBossError("The action no longer exists.") }
        if !record.canUndo { throw SlothBossError("Only applied actions can be undone.") }
        guard let resultText = record.resultJSON, let result = try? JSON.parse(resultText).objectValue,
              let expected = result["updated_at"]?.stringValue else {
            throw SlothBossError("This action has no undo information.")
        }
        let undo = record.undoJSON.flatMap { try? JSON.parse($0).objectValue }

        try ws.write(.all) { db in
            switch record.actionType {
            case CreateTaskPayload.typeName:
                guard let taskId = result["task_id"]?.stringValue else { throw SlothBossError("This action has no undo information.") }
                try requireUnchanged(db, "tasks", taskId, expected, "task")
                _ = try db.run("DELETE FROM tasks WHERE id = $id", ["$id": taskId])

            case CreatePagePayload.typeName:
                guard let pageId = result["page_id"]?.stringValue else { throw SlothBossError("This action has no undo information.") }
                try requireUnchanged(db, "pages", pageId, expected, "page")
                if try db.exists("SELECT 1 FROM pages WHERE parent_id = $id", ["$id": pageId]) {
                    throw SlothBossError("Cannot undo: the page now has subpages.")
                }
                if try db.exists("SELECT 1 FROM tasks WHERE page_id = $id", ["$id": pageId]) {
                    throw SlothBossError("Cannot undo: tasks are now linked to the page.")
                }
                _ = try db.run("DELETE FROM pages WHERE id = $id", ["$id": pageId])

            case DraftPageContentPayload.typeName, AddPageReferencePayload.typeName:
                guard let pageId = result["page_id"]?.stringValue, let undo,
                      let previousJSON = undo["previous_content_json"]?.stringValue else {
                    throw SlothBossError("This action has no undo information.")
                }
                try requireUnchanged(db, "pages", pageId, expected, "page")
                let restoredAt = try ws.pages.updateContent(db, id: pageId, doc: try PageDocument.parse(previousJSON))
                try rebase(db, undoneId: id, recordId: pageId, previousUpdatedAt: undo["previous_updated_at"]?.stringValue, restoredAt: restoredAt)

            case UpdateTaskPayload.typeName:
                guard let taskId = result["task_id"]?.stringValue, let undo,
                      let status = undo["status"]?.stringValue, let priority = undo["priority"]?.stringValue else {
                    throw SlothBossError("This action has no undo information.")
                }
                try requireUnchanged(db, "tasks", taskId, expected, "task")
                guard let task = try TaskStore.get(db, taskId) else { throw SlothBossError("Cannot undo: the task has already been deleted.") }
                var draft = TaskStore.toDraft(task)
                draft.status = status
                draft.priority = priority
                draft.dueDate = undo["due_date"]?.stringValue
                let restoredAt = try ws.tasks.update(db, id: taskId, draft: draft)
                try rebase(db, undoneId: id, recordId: taskId, previousUpdatedAt: undo["previous_updated_at"]?.stringValue, restoredAt: restoredAt)

            default:
                throw SlothBossError("This action type cannot be undone.")
            }
            _ = try db.run("UPDATE ai_actions SET status = 'undone', undone_at = $now WHERE id = $id", ["$now": SBClock.stamp(), "$id": id])
        }
        return try require(id)
    }

    /// An undo that restores a record to exactly the state an earlier applied action left it in
    /// (same previous updated_at) re-bases that action's expectation, so actions can be undone in
    /// reverse order. Records edited by the user in between never match and stay protected.
    private func rebase(_ db: SQLiteDatabase, undoneId: String, recordId: String, previousUpdatedAt: String?, restoredAt: String) throws {
        guard let previousUpdatedAt else { return }
        _ = try db.run("""
            UPDATE ai_actions SET result_json = json_set(result_json, '$.updated_at', $restored)
            WHERE status = 'applied' AND id <> $undone
              AND coalesce(json_extract(result_json, '$.page_id'), json_extract(result_json, '$.task_id')) = $record
              AND json_extract(result_json, '$.updated_at') = $previous
            """, ["$restored": restoredAt, "$undone": undoneId, "$record": recordId, "$previous": previousUpdatedAt])
    }

    private func requireUnchanged(_ db: SQLiteDatabase, _ table: String, _ id: String, _ expected: String, _ what: String) throws {
        precondition(table == "tasks" || table == "pages")
        guard let actual = try db.scalarString("SELECT updated_at FROM \(table) WHERE id = $id", ["$id": id]) else {
            throw SlothBossError("Cannot undo: the \(what) has already been deleted.")
        }
        if actual != expected {
            throw SlothBossError("Cannot undo: the \(what) was edited after the action was applied, and undoing would discard those edits.")
        }
    }
}
