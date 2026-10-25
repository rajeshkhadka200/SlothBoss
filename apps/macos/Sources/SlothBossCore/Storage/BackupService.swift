import Foundation

public struct BackupPage: Equatable, Sendable {
    public let id: String
    public let parentId: String?
    public let title: String
    public let content: PageDocument
    public let sortOrder: Double
    public let createdAt: String
    public let updatedAt: String
    public let isFavorite: Bool
    public let isArchived: Bool
}

public struct BackupTask: Equatable, Sendable {
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
}

public struct ValidatedBackup: Equatable, Sendable {
    public let pages: [BackupPage]
    public let tasks: [BackupTask]
    public let warnings: [String]
    public let generator: String?
    public let exportedAt: String
}

/// Versioned JSON backups (contracts/schemas/backup.schema.json). Restores are all-or-nothing:
/// the file is fully validated before anything is written, and the write is one transaction.
public enum BackupService {
    public static let format = "slothboss-backup"
    public static let formatVersion = 1
    public static let maxBackupBytes: Int64 = 512 * 1024 * 1024

    public static func export(_ ws: Workspace, generator: String) throws -> String {
        let (pages, tasks) = try ws.read { db -> ([JSON], [JSON]) in
            let p = try db.query("""
                SELECT id, parent_id, title, content_json, sort_order, created_at, updated_at, is_favorite, is_archived
                FROM pages ORDER BY created_at, id
                """) { r -> JSON in
                let content = (try? JSON.parse(r.string(3))) ?? (try! JSON.parse(PageDocument.emptyJSON))
                return .object(JSONObject([
                    ("id", .string(r.string(0))), ("parentId", .optional(r.optionalString(1))), ("title", .string(r.string(2))),
                    ("content", content), ("sortOrder", .double(r.double(4))),
                    ("createdAt", .string(r.string(5))), ("updatedAt", .string(r.string(6))),
                    ("isFavorite", .bool(r.bool(7))), ("isArchived", .bool(r.bool(8))),
                ]))
            }
            let t = try db.query("""
                SELECT id, title, description, status, priority, due_date, page_id, created_at, updated_at, completed_at
                FROM tasks ORDER BY created_at, id
                """) { r -> JSON in
                .object(JSONObject([
                    ("id", .string(r.string(0))), ("title", .string(r.string(1))), ("description", .optional(r.optionalString(2))),
                    ("status", .string(r.string(3))), ("priority", .string(r.string(4))), ("dueDate", .optional(r.optionalString(5))),
                    ("pageId", .optional(r.optionalString(6))), ("createdAt", .string(r.string(7))), ("updatedAt", .string(r.string(8))),
                    ("completedAt", .optional(r.optionalString(9))),
                ]))
            }
            return (p, t)
        }
        let root = JSON.object(JSONObject([
            ("format", .string(format)),
            ("formatVersion", .int(Int64(formatVersion))),
            ("schemaVersion", .int(Int64(Workspace.schemaVersion))),
            ("exportedAt", .string(SBClock.stamp())),
            ("generator", .string(generator)),
            ("pages", .array(pages)),
            ("tasks", .array(tasks)),
        ]))
        return root.prettySerialized()
    }

    /// Writes atomically (temporary file in the same folder, then replace).
    public static func exportToFile(_ ws: Workspace, path: String, generator: String) throws {
        let json = try export(ws, generator: generator)
        try Data(json.utf8).write(to: URL(fileURLWithPath: path), options: [.atomic])
    }

    public static func validateFile(_ path: String) throws -> ValidatedBackup {
        let fm = FileManager.default
        guard fm.fileExists(atPath: path) else { throw SlothBossError("The backup file does not exist.") }
        let attrs = try fm.attributesOfItem(atPath: path)
        if let size = (attrs[.size] as? NSNumber)?.int64Value, size > maxBackupBytes {
            throw SlothBossError("The backup file is too large to import.")
        }
        let data = try Data(contentsOf: URL(fileURLWithPath: path))
        return try validate(data: data)
    }

    public static func validate(_ json: String) throws -> ValidatedBackup {
        try validate(data: Data(json.utf8))
    }

    private struct WrongType: Error { let key: String }

    /// Typed field access with the reference's semantics: missing or null -> nil, wrong type -> error.
    private static func str(_ o: JSONObject, _ key: String) throws -> String? {
        guard let v = o[key], !v.isNull else { return nil }
        guard let s = v.stringValue else { throw WrongType(key: key) }
        return s
    }

    private static func int(_ o: JSONObject, _ key: String) throws -> Int64? {
        guard let v = o[key], !v.isNull else { return nil }
        guard let i = v.intValue else { throw WrongType(key: key) }
        return i
    }

    private static func dbl(_ o: JSONObject, _ key: String) throws -> Double? {
        guard let v = o[key], !v.isNull else { return nil }
        guard let d = v.doubleValue else { throw WrongType(key: key) }
        return d
    }

    private static func bool(_ o: JSONObject, _ key: String) throws -> Bool? {
        guard let v = o[key], !v.isNull else { return nil }
        guard let b = v.boolValue else { throw WrongType(key: key) }
        return b
    }

    public static func validate(data: Data) throws -> ValidatedBackup {
        let node: JSON
        do { node = try JSON.parse(data) } catch {
            throw SlothBossError.backupValidation(["Not valid JSON: \(error)"])
        }
        guard let root = node.objectValue else { throw SlothBossError.backupValidation(["The file is not a SlothBoss backup."]) }

        do {
            if try str(root, "format") != format {
                throw SlothBossError.backupValidation(["The file is not a SlothBoss backup (missing format marker)."])
            }
            let fv = try int(root, "formatVersion")
            if fv != Int64(formatVersion) {
                throw SlothBossError.backupValidation([
                    "Unsupported backup format version \(fv.map { String($0) } ?? "(missing)"); this app reads version \(formatVersion)."])
            }
            let sv = try int(root, "schemaVersion")
            if sv == nil || sv! < 1 || sv! > Int64(Workspace.schemaVersion) {
                throw SlothBossError.backupValidation([
                    "The backup was made with schema version \(sv.map { String($0) } ?? "(missing)"), which this app does not support."])
            }
        } catch let e as WrongType {
            throw SlothBossError.backupValidation(["'\(e.key)' has the wrong type."])
        }

        var errors: [String] = []
        var warnings: [String] = []
        var pages: [BackupPage] = []
        var tasks: [BackupTask] = []

        var pageArr: [JSON] = []
        var taskArr: [JSON] = []
        if let a = root["pages"]?.arrayValue { pageArr = a } else { errors.append("Missing 'pages' list.") }
        if let a = root["tasks"]?.arrayValue { taskArr = a } else { errors.append("Missing 'tasks' list.") }

        var pageIds = Set<String>()
        for (i, item) in pageArr.enumerated() {
            let location = "pages[\(i)]"
            guard let p = item.objectValue else { errors.append("\(location) is not an object."); continue }
            do {
                let id = try str(p, "id")
                guard let id, Ids.isValid(id) else { errors.append("\(location) has an invalid id."); continue }
                guard pageIds.insert(id).inserted else { errors.append("\(location) duplicates page id \(id)."); continue }
                let parent = try str(p, "parentId")
                if let parent, !Ids.isValid(parent) { errors.append("\(location) has an invalid parentId.") }
                let content: PageDocument
                do { content = try PageDocument.from(p["content"]) } catch let e as SlothBossError {
                    errors.append("\(location) content: \(e.message)")
                    continue
                }
                let created = try str(p, "createdAt")
                let updated = try str(p, "updatedAt")
                if !SBClock.isValidTimestamp(created) || !SBClock.isValidTimestamp(updated) {
                    errors.append("\(location) has invalid timestamps.")
                }
                pages.append(BackupPage(
                    id: id, parentId: parent, title: try str(p, "title") ?? "", content: content,
                    sortOrder: try dbl(p, "sortOrder") ?? Double(i), createdAt: created ?? "", updatedAt: updated ?? "",
                    isFavorite: try bool(p, "isFavorite") ?? false, isArchived: try bool(p, "isArchived") ?? false))
            } catch let e as WrongType {
                errors.append("\(location): '\(e.key)' has the wrong type.")
            }
        }

        for p in pages {
            if let parent = p.parentId, !pageIds.contains(parent) {
                errors.append("Page \(p.id) refers to a missing parent \(parent).")
            }
        }
        var parentOf: [String: String?] = [:]
        for p in pages { parentOf[p.id] = p.parentId }
        for p in pages {
            var seen: Set<String> = [p.id]
            var cur = p.parentId
            while let c = cur, let next = parentOf[c] {
                if !seen.insert(c).inserted { errors.append("Page \(p.id) is part of a parent cycle."); break }
                cur = next
            }
        }

        var taskIds = Set<String>()
        for (i, item) in taskArr.enumerated() {
            let location = "tasks[\(i)]"
            guard let t = item.objectValue else { errors.append("\(location) is not an object."); continue }
            do {
                let id = try str(t, "id")
                guard let id, Ids.isValid(id) else { errors.append("\(location) has an invalid id."); continue }
                guard taskIds.insert(id).inserted else { errors.append("\(location) duplicates task id \(id)."); continue }
                var draft = TaskDraft(try str(t, "title") ?? "", description: try str(t, "description"), status: try str(t, "status") ?? "",
                                      priority: try str(t, "priority") ?? "", dueDate: try str(t, "dueDate"), pageId: try str(t, "pageId"))
                do { draft = try TaskStore.validate(draft) } catch let e as SlothBossError {
                    errors.append("\(location): \(e.message)")
                    continue
                }
                if let pageId = draft.pageId, !pageIds.contains(pageId) {
                    errors.append("\(location) refers to a missing page \(pageId).")
                    continue
                }
                let created = try str(t, "createdAt")
                let updated = try str(t, "updatedAt")
                guard let created, let updated, SBClock.isValidTimestamp(created), SBClock.isValidTimestamp(updated) else {
                    errors.append("\(location) has invalid timestamps.")
                    continue
                }
                var completed = try str(t, "completedAt")
                if draft.status == TaskState.done && completed == nil {
                    completed = updated
                    warnings.append("Task “\(draft.title)” was done without a completion time; used its last update time.")
                }
                if draft.status != TaskState.done && completed != nil {
                    completed = nil
                    warnings.append("Task “\(draft.title)” is not done but had a completion time; cleared it.")
                }
                tasks.append(BackupTask(id: id, title: draft.title, description: draft.description, status: draft.status,
                                        priority: draft.priority, dueDate: draft.dueDate, pageId: draft.pageId,
                                        createdAt: created, updatedAt: updated, completedAt: completed))
            } catch let e as WrongType {
                errors.append("\(location): '\(e.key)' has the wrong type.")
            }
        }

        if !errors.isEmpty { throw SlothBossError.backupValidation(errors) }
        let generator = (try? str(root, "generator")) ?? nil
        let exportedAt = ((try? str(root, "exportedAt")) ?? nil) ?? ""
        return ValidatedBackup(pages: pages, tasks: tasks, warnings: warnings, generator: generator, exportedAt: exportedAt)
    }

    /// Replaces all pages, tasks, links and AI history in `ws` with the backup's contents, in a single
    /// transaction. Callers must confirm with the user and snapshot the workspace first. Settings are kept.
    public static func restoreReplacing(_ ws: Workspace, _ backup: ValidatedBackup) throws {
        try ws.write(.all) { db in
            try db.run("DELETE FROM ai_actions")
            try db.run("DELETE FROM tasks")
            try db.run("DELETE FROM page_links")
            try db.run("DELETE FROM pages")

            // Parents before children (breadth-first from the roots).
            var byParent: [String: [BackupPage]] = [:]
            var roots: [BackupPage] = []
            for p in backup.pages {
                if let parent = p.parentId { byParent[parent, default: []].append(p) } else { roots.append(p) }
            }
            var ordered: [BackupPage] = []
            var queue = roots
            var head = 0
            while head < queue.count {
                let p = queue[head]
                head += 1
                ordered.append(p)
                queue.append(contentsOf: byParent[p.id] ?? [])
            }
            for p in ordered {
                try db.run("""
                    INSERT INTO pages (id, parent_id, title, content_json, content_text, sort_order, created_at, updated_at, is_favorite, is_archived)
                    VALUES ($id, $parent, $title, $json, $text, $order, $created, $updated, $fav, $arch)
                    """,
                    ["$id": p.id, "$parent": p.parentId, "$title": p.title, "$json": p.content.toJSON(), "$text": p.content.plainText(),
                     "$order": p.sortOrder, "$created": p.createdAt, "$updated": p.updatedAt, "$fav": p.isFavorite, "$arch": p.isArchived])
            }
            for p in ordered {
                for target in p.content.pageReferences() {
                    try db.run("INSERT OR IGNORE INTO page_links (source_page_id, target_page_id) SELECT $s, id FROM pages WHERE id = $t AND id <> $s",
                               ["$s": p.id, "$t": target])
                }
            }
            for t in backup.tasks {
                try db.run("""
                    INSERT INTO tasks (id, title, description, status, priority, due_date, page_id, created_at, updated_at, completed_at)
                    VALUES ($id, $title, $desc, $status, $priority, $due, $page, $created, $updated, $completed)
                    """,
                    ["$id": t.id, "$title": t.title, "$desc": t.description, "$status": t.status, "$priority": t.priority,
                     "$due": t.dueDate, "$page": t.pageId, "$created": t.createdAt, "$updated": t.updatedAt, "$completed": t.completedAt])
            }
        }
    }

    /// Safe file name for exports: strips path separators and characters Windows/macOS reject.
    public static func safeFileName(_ title: String, extension ext: String) -> String {
        let invalid: Set<Character> = ["/", "\\", ":", "*", "?", "\"", "<", ">", "|"]
        let source = title.isBlank ? "Untitled" : title
        var name = String(source.map { ch -> Character in
            if invalid.contains(ch) { return "-" }
            if ch.unicodeScalars.contains(where: { $0.properties.generalCategory == .control }) { return "-" }
            return ch
        }).trimming([" ", "."])
        if name.isEmpty { name = "Untitled" }
        if name.count > 80 { name = name.prefixString(80).trimmed }
        return name + ext
    }
}
