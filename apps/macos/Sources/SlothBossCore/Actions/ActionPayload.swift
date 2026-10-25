import Foundation

/// Typed, validated action payloads (contracts/schemas/action.schema.json). The application builds
/// these from validated model output; they are re-parsed strictly whenever read back from the database.
public enum ActionPayload: Equatable, Sendable {
    case createTask(CreateTaskPayload)
    case createPage(CreatePagePayload)
    case draftPageContent(DraftPageContentPayload)
    case addPageReference(AddPageReferencePayload)
    case updateTask(UpdateTaskPayload)

    public var type: String {
        switch self {
        case .createTask: return CreateTaskPayload.typeName
        case .createPage: return CreatePagePayload.typeName
        case .draftPageContent: return DraftPageContentPayload.typeName
        case .addPageReference: return AddPageReferencePayload.typeName
        case .updateTask: return UpdateTaskPayload.typeName
        }
    }

    public func toJSON() -> String {
        var o = JSONObject()
        o["type"] = .string(type)
        switch self {
        case .createTask(let p): p.write(&o)
        case .createPage(let p): p.write(&o)
        case .draftPageContent(let p): p.write(&o)
        case .addPageReference(let p): p.write(&o)
        case .updateTask(let p): p.write(&o)
        }
        return JSON.object(o).serialized()
    }

    /// Structural validation (no database access).
    public func validate() throws {
        switch self {
        case .createTask(let p): try p.validate()
        case .createPage(let p): try p.validate()
        case .draftPageContent(let p): try p.validate()
        case .addPageReference(let p): try p.validate()
        case .updateTask(let p): try p.validate()
        }
    }

    public static func parse(_ json: String) throws -> ActionPayload {
        let node: JSON
        do { node = try JSON.parse(json) } catch {
            throw SlothBossError.actionValidation("Action payload is not valid JSON: \(error)")
        }
        guard let o = node.objectValue else { throw SlothBossError.actionValidation("Action payload must be an object.") }
        guard let type = try str(o, "type") else { throw SlothBossError.actionValidation("Action payload is missing 'type'.") }
        let payload: ActionPayload
        switch type {
        case CreateTaskPayload.typeName: payload = .createTask(try CreateTaskPayload.from(o))
        case CreatePagePayload.typeName: payload = .createPage(try CreatePagePayload.from(o))
        case DraftPageContentPayload.typeName: payload = .draftPageContent(try DraftPageContentPayload.from(o))
        case AddPageReferencePayload.typeName: payload = .addPageReference(try AddPageReferencePayload.from(o))
        case UpdateTaskPayload.typeName: payload = .updateTask(try UpdateTaskPayload.from(o))
        default: throw SlothBossError.actionValidation("Unknown action type '\(type)'.")
        }
        try payload.validate()
        return payload
    }

    // MARK: helpers shared by the payload types

    static func only(_ o: JSONObject, _ keys: Set<String>) throws {
        for k in o.keys where k != "type" && !keys.contains(k) {
            throw SlothBossError.actionValidation("Unexpected action property '\(k)'.")
        }
    }

    static func str(_ o: JSONObject, _ key: String) throws -> String? {
        guard let v = o[key], !v.isNull else { return nil }
        guard let s = v.stringValue else { throw SlothBossError.actionValidation("Action property '\(key)' must be a string.") }
        return s
    }

    static func req(_ o: JSONObject, _ key: String) throws -> String {
        guard let s = try str(o, key) else { throw SlothBossError.actionValidation("Action payload is missing '\(key)'.") }
        return s
    }

    static func requireId(_ id: String?, _ what: String) throws {
        if !Ids.isValid(id) { throw SlothBossError.actionValidation("Action references an invalid \(what) id.") }
    }

    static func requireTitle(_ title: String, max: Int) throws {
        if title.isBlank { throw SlothBossError.actionValidation("Title must not be empty.") }
        if title.count > max { throw SlothBossError.actionValidation("Title must be at most \(max) characters.") }
    }
}

public struct CreateTaskPayload: Equatable, Sendable {
    public static let typeName = "create_task"
    public var title: String
    public var description: String?
    public var priority: String
    public var dueDate: String?
    public var pageId: String?

    public init(title: String, description: String?, priority: String, dueDate: String?, pageId: String?) {
        self.title = title
        self.description = description
        self.priority = priority
        self.dueDate = dueDate
        self.pageId = pageId
    }

    func write(_ o: inout JSONObject) {
        o["title"] = .string(title)
        o["description"] = .optional(description)
        o["priority"] = .string(priority)
        o["due_date"] = .optional(dueDate)
        o["page_id"] = .optional(pageId)
    }

    static func from(_ o: JSONObject) throws -> CreateTaskPayload {
        try ActionPayload.only(o, ["title", "description", "priority", "due_date", "page_id"])
        return CreateTaskPayload(title: try ActionPayload.req(o, "title"), description: try ActionPayload.str(o, "description"),
                                 priority: try ActionPayload.req(o, "priority"), dueDate: try ActionPayload.str(o, "due_date"),
                                 pageId: try ActionPayload.str(o, "page_id"))
    }

    public func validate() throws {
        try ActionPayload.requireTitle(title, max: TaskStore.maxTitleLength)
        if let description, description.count > TaskStore.maxDescriptionLength {
            throw SlothBossError.actionValidation("Description is too long.")
        }
        if !TaskPriorityLevel.isValid(priority) { throw SlothBossError.actionValidation("Unknown priority '\(priority)'.") }
        if let dueDate, !SBClock.isValidDate(dueDate) { throw SlothBossError.actionValidation("Invalid due date '\(dueDate)'.") }
        if let pageId { try ActionPayload.requireId(pageId, "page") }
    }

    public func toDraft() -> TaskDraft {
        TaskDraft(title, description: description, status: TaskState.todo, priority: priority, dueDate: dueDate, pageId: pageId)
    }
}

public struct CreatePagePayload: Equatable, Sendable {
    public static let typeName = "create_page"
    public var title: String
    public var parentId: String?
    public var content: PageDocument

    public init(title: String, parentId: String?, content: PageDocument) {
        self.title = title
        self.parentId = parentId
        self.content = content
    }

    func write(_ o: inout JSONObject) {
        o["title"] = .string(title)
        o["parent_id"] = .optional(parentId)
        o["content"] = content.toJSONValue()
    }

    static func from(_ o: JSONObject) throws -> CreatePagePayload {
        try ActionPayload.only(o, ["title", "parent_id", "content"])
        let content: PageDocument
        do { content = try PageDocument.from(o["content"]) } catch let e as SlothBossError {
            throw SlothBossError.actionValidation("Invalid page content: " + e.message)
        }
        return CreatePagePayload(title: try ActionPayload.req(o, "title"), parentId: try ActionPayload.str(o, "parent_id"), content: content)
    }

    public func validate() throws {
        try ActionPayload.requireTitle(title, max: PageStore.maxTitleLength)
        if let parentId { try ActionPayload.requireId(parentId, "page") }
    }
}

/// Appends blocks to the end of an existing page.
public struct DraftPageContentPayload: Equatable, Sendable {
    public static let typeName = "draft_page_content"
    public var pageId: String
    public var blocks: [Block]

    public init(pageId: String, blocks: [Block]) {
        self.pageId = pageId
        self.blocks = blocks
    }

    func write(_ o: inout JSONObject) {
        o["page_id"] = .string(pageId)
        o["blocks"] = .array(blocks.map(PageDocument.blockJSON))
    }

    static func from(_ o: JSONObject) throws -> DraftPageContentPayload {
        try ActionPayload.only(o, ["page_id", "blocks"])
        let doc: PageDocument
        do {
            doc = try PageDocument.from(.object(JSONObject([("version", .int(1)), ("blocks", o["blocks"] ?? .null)])))
        } catch let e as SlothBossError {
            throw SlothBossError.actionValidation("Invalid blocks: " + e.message)
        }
        return DraftPageContentPayload(pageId: try ActionPayload.req(o, "page_id"), blocks: doc.blocks)
    }

    public func validate() throws {
        try ActionPayload.requireId(pageId, "page")
        if blocks.isEmpty { throw SlothBossError.actionValidation("Draft content must contain at least one block.") }
    }
}

/// Appends a paragraph linking page_id to target_page_id.
public struct AddPageReferencePayload: Equatable, Sendable {
    public static let typeName = "add_page_reference"
    public var pageId: String
    public var targetPageId: String

    public init(pageId: String, targetPageId: String) {
        self.pageId = pageId
        self.targetPageId = targetPageId
    }

    func write(_ o: inout JSONObject) {
        o["page_id"] = .string(pageId)
        o["target_page_id"] = .string(targetPageId)
    }

    static func from(_ o: JSONObject) throws -> AddPageReferencePayload {
        try ActionPayload.only(o, ["page_id", "target_page_id"])
        return AddPageReferencePayload(pageId: try ActionPayload.req(o, "page_id"), targetPageId: try ActionPayload.req(o, "target_page_id"))
    }

    public func validate() throws {
        try ActionPayload.requireId(pageId, "page")
        try ActionPayload.requireId(targetPageId, "target page")
        if pageId == targetPageId { throw SlothBossError.actionValidation("A page cannot reference itself.") }
    }
}

/// Only status, priority and due date are changeable by AI actions.
public struct UpdateTaskPayload: Equatable, Sendable {
    public static let typeName = "update_task"
    public var taskId: String
    public var status: String?
    public var priority: String?
    public var changesDueDate: Bool
    public var dueDate: String?

    public init(taskId: String, status: String?, priority: String?, changesDueDate: Bool, dueDate: String?) {
        self.taskId = taskId
        self.status = status
        self.priority = priority
        self.changesDueDate = changesDueDate
        self.dueDate = dueDate
    }

    func write(_ o: inout JSONObject) {
        o["task_id"] = .string(taskId)
        var changes = JSONObject()
        if let status { changes["status"] = .string(status) }
        if let priority { changes["priority"] = .string(priority) }
        if changesDueDate { changes["due_date"] = .optional(dueDate) }
        o["changes"] = .object(changes)
    }

    static func from(_ o: JSONObject) throws -> UpdateTaskPayload {
        try ActionPayload.only(o, ["task_id", "changes"])
        guard let ch = o["changes"]?.objectValue else {
            throw SlothBossError.actionValidation("update_task requires a 'changes' object.")
        }
        for k in ch.keys where !["status", "priority", "due_date"].contains(k) {
            throw SlothBossError.actionValidation("AI actions may not change the task field '\(k)'.")
        }
        return UpdateTaskPayload(taskId: try ActionPayload.req(o, "task_id"), status: try ActionPayload.str(ch, "status"),
                                 priority: try ActionPayload.str(ch, "priority"), changesDueDate: ch.contains("due_date"),
                                 dueDate: try ActionPayload.str(ch, "due_date"))
    }

    public func validate() throws {
        try ActionPayload.requireId(taskId, "task")
        if status == nil && priority == nil && !changesDueDate {
            throw SlothBossError.actionValidation("update_task must change at least one field.")
        }
        if let status, !TaskState.isValid(status) { throw SlothBossError.actionValidation("Unknown status '\(status)'.") }
        if let priority, !TaskPriorityLevel.isValid(priority) { throw SlothBossError.actionValidation("Unknown priority '\(priority)'.") }
        if let dueDate, !SBClock.isValidDate(dueDate) { throw SlothBossError.actionValidation("Invalid due date '\(dueDate)'.") }
    }
}
