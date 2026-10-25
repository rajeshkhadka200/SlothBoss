import Foundation

public enum TaskState {
    public static let todo = "todo", inProgress = "in_progress", done = "done"
    public static let all = [todo, inProgress, done]
    public static func isValid(_ s: String?) -> Bool { s.map { all.contains($0) } ?? false }
    public static func label(_ s: String) -> String {
        switch s {
        case todo: return "To do"
        case inProgress: return "In progress"
        case done: return "Done"
        default: return s
        }
    }
}

public enum TaskPriorityLevel {
    public static let low = "low", medium = "medium", high = "high", urgent = "urgent"
    public static let all = [low, medium, high, urgent]
    public static func isValid(_ s: String?) -> Bool { s.map { all.contains($0) } ?? false }
    public static func label(_ s: String) -> String {
        guard let first = s.first else { return s }
        return String(first).uppercased() + s.dropFirst()
    }
}

public struct TaskItem: Equatable, Hashable, Identifiable, Sendable {
    public let id: String
    public let title: String
    public let description: String?
    public let status: String
    public let priority: String
    public let dueDate: String?
    public let pageId: String?
    public let createdAt: String
    public let updatedAt: String
    public let completedAt: String?

    public var isDone: Bool { status == TaskState.done }
}

/// Fields a user (or an approved AI action) supplies when creating or editing a task.
public struct TaskDraft: Equatable, Sendable {
    public var title: String
    public var description: String?
    public var status: String
    public var priority: String
    public var dueDate: String?
    public var pageId: String?

    public init(_ title: String, description: String? = nil, status: String = TaskState.todo, priority: String = TaskPriorityLevel.medium,
                dueDate: String? = nil, pageId: String? = nil) {
        self.title = title
        self.description = description
        self.status = status
        self.priority = priority
        self.dueDate = dueDate
        self.pageId = pageId
    }
}

public enum DueFilter: String, CaseIterable, Sendable {
    case any, overdue, today, next7Days, noDate

    public var label: String {
        switch self {
        case .any: return "Any date"
        case .overdue: return "Overdue"
        case .today: return "Due today"
        case .next7Days: return "Next 7 days"
        case .noDate: return "No date"
        }
    }
}

public struct TaskFilter: Equatable, Sendable {
    public var status: String?
    public var priority: String?
    public var due: DueFilter
    public var pageId: String?
    public var hideDone: Bool

    public init(status: String? = nil, priority: String? = nil, due: DueFilter = .any, pageId: String? = nil, hideDone: Bool = false) {
        self.status = status
        self.priority = priority
        self.due = due
        self.pageId = pageId
        self.hideDone = hideDone
    }
}

public final class TaskStore: @unchecked Sendable {
    public static let maxTitleLength = 200
    public static let maxDescriptionLength = 4000

    private unowned let ws: Workspace
    init(_ ws: Workspace) { self.ws = ws }

    static let columns = "id, title, description, status, priority, due_date, page_id, created_at, updated_at, completed_at"

    static func map(_ r: SQLRow) -> TaskItem {
        TaskItem(id: r.string(0), title: r.string(1), description: r.optionalString(2), status: r.string(3), priority: r.string(4),
                 dueDate: r.optionalString(5), pageId: r.optionalString(6), createdAt: r.string(7), updatedAt: r.string(8),
                 completedAt: r.optionalString(9))
    }

    /// Validates and normalizes a draft. Throws a user-readable SlothBossError.
    public static func validate(_ d: TaskDraft) throws -> TaskDraft {
        let title = d.title.replacingOccurrences(of: "\r", with: " ").replacingOccurrences(of: "\n", with: " ").trimmed
        if title.isEmpty { throw SlothBossError("A task needs a title.") }
        if title.count > maxTitleLength { throw SlothBossError("Task titles are limited to \(maxTitleLength) characters.") }
        let desc: String? = (d.description?.isBlank ?? true) ? nil : d.description!.trimmed
        if let desc, desc.count > maxDescriptionLength {
            throw SlothBossError("Task descriptions are limited to \(maxDescriptionLength) characters.")
        }
        if !TaskState.isValid(d.status) { throw SlothBossError("Unknown task status '\(d.status)'.") }
        if !TaskPriorityLevel.isValid(d.priority) { throw SlothBossError("Unknown task priority '\(d.priority)'.") }
        let due: String? = (d.dueDate?.isBlank ?? true) ? nil : d.dueDate!.trimmed
        if let due, !SBClock.isValidDate(due) { throw SlothBossError("'\(due)' is not a valid date (expected YYYY-MM-DD).") }
        var out = d
        out.title = title
        out.description = desc
        out.dueDate = due
        return out
    }

    @discardableResult
    public func create(_ draft: TaskDraft) throws -> TaskItem {
        let id = Ids.new()
        try ws.write(.tasks) { db in try insert(db, id: id, draft: draft) }
        guard let task = try get(id) else { throw SlothBossError("The task could not be created.") }
        return task
    }

    func insert(_ db: SQLiteDatabase, id: String, draft: TaskDraft) throws {
        let d = try TaskStore.validate(draft)
        if let pageId = d.pageId, try !db.exists("SELECT 1 FROM pages WHERE id = $p", ["$p": pageId]) {
            throw SlothBossError("The linked page no longer exists.")
        }
        let now = SBClock.stamp()
        let completed: String? = d.status == TaskState.done ? now : nil
        try db.run("""
            INSERT INTO tasks (\(TaskStore.columns))
            VALUES ($id, $title, $desc, $status, $priority, $due, $page, $now, $now, $completed)
            """,
            ["$id": id, "$title": d.title, "$desc": d.description, "$status": d.status, "$priority": d.priority,
             "$due": d.dueDate, "$page": d.pageId, "$now": now, "$completed": completed])
    }

    public func get(_ id: String) throws -> TaskItem? {
        try ws.read { db in try TaskStore.get(db, id) }
    }

    static func get(_ db: SQLiteDatabase, _ id: String) throws -> TaskItem? {
        try db.query("SELECT \(columns) FROM tasks WHERE id = $id", ["$id": id], map).first
    }

    /// Replaces all editable fields. completed_at is set when a task becomes done and cleared when reopened.
    @discardableResult
    public func update(_ id: String, _ draft: TaskDraft) throws -> TaskItem {
        try ws.write(.tasks) { db in _ = try update(db, id: id, draft: draft) }
        guard let task = try get(id) else { throw SlothBossError("The task no longer exists.") }
        return task
    }

    func update(_ db: SQLiteDatabase, id: String, draft: TaskDraft) throws -> String {
        let d = try TaskStore.validate(draft)
        guard let existing = try TaskStore.get(db, id) else { throw SlothBossError("The task no longer exists.") }
        if let pageId = d.pageId, try !db.exists("SELECT 1 FROM pages WHERE id = $p", ["$p": pageId]) {
            throw SlothBossError("The linked page no longer exists.")
        }
        let now = SBClock.stamp()
        let completed: String? = d.status == TaskState.done ? (existing.completedAt ?? now) : nil
        try db.run("""
            UPDATE tasks SET title = $title, description = $desc, status = $status, priority = $priority,
                due_date = $due, page_id = $page, updated_at = $now, completed_at = $completed
            WHERE id = $id
            """,
            ["$title": d.title, "$desc": d.description, "$status": d.status, "$priority": d.priority,
             "$due": d.dueDate, "$page": d.pageId, "$now": now, "$completed": completed, "$id": id])
        return now
    }

    @discardableResult
    public func setStatus(_ id: String, _ status: String) throws -> TaskItem {
        guard let t = try get(id) else { throw SlothBossError("The task no longer exists.") }
        var draft = TaskStore.toDraft(t)
        draft.status = status
        return try update(id, draft)
    }

    public static func toDraft(_ t: TaskItem) -> TaskDraft {
        TaskDraft(t.title, description: t.description, status: t.status, priority: t.priority, dueDate: t.dueDate, pageId: t.pageId)
    }

    public func delete(_ id: String) throws {
        try ws.write(.tasks) { db in
            if try db.run("DELETE FROM tasks WHERE id = $id", ["$id": id]) == 0 {
                throw SlothBossError("The task no longer exists.")
            }
        }
    }

    public func list(_ filter: TaskFilter = TaskFilter()) throws -> [TaskItem] {
        var conditions: [String] = []
        var params: SQLParams = [:]
        if let status = filter.status { conditions.append("status = $status"); params["$status"] = status }
        if filter.hideDone { conditions.append("status <> 'done'") }
        if let priority = filter.priority { conditions.append("priority = $priority"); params["$priority"] = priority }
        if let pageId = filter.pageId { conditions.append("page_id = $page"); params["$page"] = pageId }
        let today = SBClock.today()
        switch filter.due {
        case .overdue:
            conditions.append("due_date < $today AND status <> 'done'")
            params["$today"] = today
        case .today:
            conditions.append("due_date = $today")
            params["$today"] = today
        case .next7Days:
            conditions.append("due_date >= $today AND due_date <= $week")
            params["$today"] = today
            params["$week"] = SBClock.addDays(today, 7) ?? today
        case .noDate:
            conditions.append("due_date IS NULL")
        case .any:
            break
        }
        let whereClause = conditions.isEmpty ? "" : "WHERE " + conditions.joined(separator: " AND ")
        let sql = """
            SELECT \(TaskStore.columns) FROM tasks
            \(whereClause)
            ORDER BY CASE status WHEN 'in_progress' THEN 0 WHEN 'todo' THEN 1 ELSE 2 END,
                     due_date IS NULL, due_date,
                     CASE priority WHEN 'urgent' THEN 0 WHEN 'high' THEN 1 WHEN 'medium' THEN 2 ELSE 3 END,
                     created_at
            """
        let bound = params
        return try ws.read { db in try db.query(sql, bound, TaskStore.map) }
    }

    public func forPage(_ pageId: String) throws -> [TaskItem] { try list(TaskFilter(pageId: pageId)) }
}

public final class SettingsStore: @unchecked Sendable {
    public static let selectedModel = "ai.model"
    public static let ollamaEndpoint = "ai.endpoint"
    public static let aiTimeoutSeconds = "ai.timeout_seconds"
    public static let theme = "ui.theme"
    public static let editorFontSize = "editor.font_size"

    private unowned let ws: Workspace
    init(_ ws: Workspace) { self.ws = ws }

    public func get(_ key: String) throws -> String? {
        try ws.read { db in try db.scalarString("SELECT value FROM settings WHERE key = $k", ["$k": key]) }
    }

    public func get(_ key: String, fallback: String) throws -> String { try get(key) ?? fallback }

    public func set(_ key: String, _ value: String?) throws {
        try ws.write(.settings) { db in
            if let value {
                _ = try db.run("INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value",
                           ["$k": key, "$v": value])
            } else {
                _ = try db.run("DELETE FROM settings WHERE key = $k", ["$k": key])
            }
        }
    }
}
