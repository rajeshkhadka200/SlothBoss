import Foundation

public struct PageSummary: Equatable, Hashable, Identifiable, Sendable {
    public let id: String
    public let parentId: String?
    public let title: String
    public let sortOrder: Double
    public let createdAt: String
    public let updatedAt: String
    public let isFavorite: Bool
    public let isArchived: Bool

    public var displayTitle: String { title.isBlank ? "Untitled" : title }
}

public struct Page: Equatable, Sendable {
    public let id: String
    public let parentId: String?
    public let title: String
    public let contentJSON: String
    public let contentText: String
    public let sortOrder: Double
    public let createdAt: String
    public let updatedAt: String
    public let isFavorite: Bool
    public let isArchived: Bool

    public var displayTitle: String { title.isBlank ? "Untitled" : title }
    public func document() throws -> PageDocument { try PageDocument.parse(contentJSON) }
    public var summary: PageSummary {
        PageSummary(id: id, parentId: parentId, title: title, sortOrder: sortOrder, createdAt: createdAt,
                    updatedAt: updatedAt, isFavorite: isFavorite, isArchived: isArchived)
    }
}

/// Pages form a forest (parent_id NULL = top level). Deleting a page deletes its whole subtree
/// and unlinks (does not delete) tasks attached to any page in it. Archiving applies to the subtree.
public final class PageStore: @unchecked Sendable {
    public static let maxTitleLength = 300

    private unowned let ws: Workspace
    init(_ ws: Workspace) { self.ws = ws }

    static let summaryColumns = "id, parent_id, title, sort_order, created_at, updated_at, is_favorite, is_archived"
    static let prefixedSummaryColumns = summaryColumns.components(separatedBy: ", ").map { "p." + $0 }.joined(separator: ", ")
    static let fullColumns = "id, parent_id, title, content_json, content_text, sort_order, created_at, updated_at, is_favorite, is_archived"

    static func mapSummary(_ r: SQLRow) -> PageSummary {
        PageSummary(id: r.string(0), parentId: r.optionalString(1), title: r.string(2), sortOrder: r.double(3),
                    createdAt: r.string(4), updatedAt: r.string(5), isFavorite: r.bool(6), isArchived: r.bool(7))
    }

    static func mapPage(_ r: SQLRow) -> Page {
        Page(id: r.string(0), parentId: r.optionalString(1), title: r.string(2), contentJSON: r.string(3), contentText: r.string(4),
             sortOrder: r.double(5), createdAt: r.string(6), updatedAt: r.string(7), isFavorite: r.bool(8), isArchived: r.bool(9))
    }

    static func cleanTitle(_ title: String) -> String {
        let t = title.replacingOccurrences(of: "\r", with: " ").replacingOccurrences(of: "\n", with: " ").trimmed
        return t.count > maxTitleLength ? t.prefixString(maxTitleLength) : t
    }

    @discardableResult
    public func create(_ title: String, parentId: String? = nil, content: PageDocument? = nil) throws -> Page {
        let id = Ids.new()
        try ws.write(.pages) { db in try insert(db, id: id, title: title, parentId: parentId, content: content) }
        guard let page = try get(id) else { throw SlothBossError("The page could not be created.") }
        return page
    }

    func insert(_ db: SQLiteDatabase, id: String, title: String, parentId: String?, content: PageDocument?) throws {
        if let parentId, try !db.exists("SELECT 1 FROM pages WHERE id = $p", ["$p": parentId]) {
            throw SlothBossError("The parent page no longer exists.")
        }
        let doc = (content ?? PageDocument()).normalized()
        let now = SBClock.stamp()
        let order = try db.scalarDouble("SELECT coalesce(max(sort_order), 0) + 1 FROM pages WHERE parent_id IS $p", ["$p": parentId])
        try db.run("""
            INSERT INTO pages (id, parent_id, title, content_json, content_text, sort_order, created_at, updated_at)
            VALUES ($id, $parent, $title, $json, $text, $order, $now, $now)
            """,
            ["$id": id, "$parent": parentId, "$title": PageStore.cleanTitle(title), "$json": doc.toJSON(),
             "$text": doc.plainText(), "$order": order, "$now": now])
        try syncLinks(db, id: id, doc: doc)
    }

    public func get(_ id: String) throws -> Page? {
        try ws.read { db in try PageStore.get(db, id) }
    }

    static func get(_ db: SQLiteDatabase, _ id: String) throws -> Page? {
        try db.query("SELECT \(fullColumns) FROM pages WHERE id = $id", ["$id": id], mapPage).first
    }

    public func exists(_ id: String) throws -> Bool {
        try ws.read { db in try db.exists("SELECT 1 FROM pages WHERE id = $id", ["$id": id]) }
    }

    public func list(includeArchived: Bool = false) throws -> [PageSummary] {
        let filter = includeArchived ? "" : "WHERE is_archived = 0"
        return try ws.read { db in
            try db.query("SELECT \(PageStore.summaryColumns) FROM pages \(filter) ORDER BY sort_order, created_at", [:], PageStore.mapSummary)
        }
    }

    /// Only the roots of archived subtrees: archived pages whose parent is not archived.
    public func archived() throws -> [PageSummary] {
        try ws.read { db in
            try db.query("""
                SELECT \(PageStore.summaryColumns) FROM pages p WHERE is_archived = 1
                  AND (parent_id IS NULL OR NOT EXISTS (SELECT 1 FROM pages q WHERE q.id = p.parent_id AND q.is_archived = 1))
                ORDER BY updated_at DESC
                """, [:], PageStore.mapSummary)
        }
    }

    public func favorites() throws -> [PageSummary] {
        try ws.read { db in
            try db.query("SELECT \(PageStore.summaryColumns) FROM pages WHERE is_favorite = 1 AND is_archived = 0 ORDER BY title", [:], PageStore.mapSummary)
        }
    }

    public func recent(limit: Int = 8) throws -> [PageSummary] {
        try ws.read { db in
            try db.query("SELECT \(PageStore.summaryColumns) FROM pages WHERE is_archived = 0 ORDER BY updated_at DESC LIMIT $n",
                         ["$n": limit], PageStore.mapSummary)
        }
    }

    public func children(_ id: String) throws -> [PageSummary] {
        try ws.read { db in
            try db.query("SELECT \(PageStore.summaryColumns) FROM pages WHERE parent_id = $id AND is_archived = 0 ORDER BY sort_order",
                         ["$id": id], PageStore.mapSummary)
        }
    }

    /// Ancestors from the top-level page down to (and including) the page itself.
    public func breadcrumb(_ id: String) throws -> [PageSummary] {
        try ws.read { db in
            try db.query("""
                WITH RECURSIVE chain(id, depth) AS (
                    SELECT $id, 0
                    UNION ALL
                    SELECT p.parent_id, chain.depth + 1 FROM pages p JOIN chain ON p.id = chain.id
                    WHERE p.parent_id IS NOT NULL AND chain.depth < 1000
                )
                SELECT \(PageStore.prefixedSummaryColumns)
                FROM chain JOIN pages p ON p.id = chain.id ORDER BY chain.depth DESC
                """, ["$id": id], PageStore.mapSummary)
        }
    }

    /// Saves new content. Returns the new updated_at.
    @discardableResult
    public func updateContent(_ id: String, _ doc: PageDocument) throws -> String {
        try ws.write(.pages) { db in try updateContent(db, id: id, doc: doc) }
    }

    func updateContent(_ db: SQLiteDatabase, id: String, doc input: PageDocument) throws -> String {
        let doc = input.normalized()
        let now = SBClock.stamp()
        let n = try db.run("UPDATE pages SET content_json = $json, content_text = $text, updated_at = $now WHERE id = $id",
                           ["$json": doc.toJSON(), "$text": doc.plainText(), "$now": now, "$id": id])
        if n == 0 { throw SlothBossError("The page no longer exists.") }
        try syncLinks(db, id: id, doc: doc)
        return now
    }

    private func syncLinks(_ db: SQLiteDatabase, id: String, doc: PageDocument) throws {
        try db.run("DELETE FROM page_links WHERE source_page_id = $id", ["$id": id])
        for target in doc.pageReferences() where target != id {
            // Links to pages that don't exist (e.g. deleted) are kept in content but not indexed.
            try db.run("INSERT OR IGNORE INTO page_links (source_page_id, target_page_id) SELECT $s, id FROM pages WHERE id = $t",
                       ["$s": id, "$t": target])
        }
    }

    public func rename(_ id: String, _ title: String) throws {
        try ws.write(.pages) { db in
            let n = try db.run("UPDATE pages SET title = $t, updated_at = $now WHERE id = $id",
                               ["$t": PageStore.cleanTitle(title), "$now": SBClock.stamp(), "$id": id])
            if n == 0 { throw SlothBossError("The page no longer exists.") }
        }
    }

    public func setFavorite(_ id: String, _ favorite: Bool) throws {
        try ws.write(.pages) { db in
            _ = try db.run("UPDATE pages SET is_favorite = $f WHERE id = $id", ["$f": favorite, "$id": id])
        }
    }

    static let subtreeCTE = """
        WITH RECURSIVE subtree(id) AS (
            SELECT $id
            UNION
            SELECT p.id FROM pages p JOIN subtree s ON p.parent_id = s.id
        )

        """

    public func archive(_ id: String) throws {
        try ws.write([.pages, .tasks]) { db in
            _ = try db.run(PageStore.subtreeCTE + "UPDATE pages SET is_archived = 1, updated_at = $now WHERE id IN (SELECT id FROM subtree)",
                           ["$id": id, "$now": SBClock.stamp()])
        }
    }

    /// Restores the subtree. If the page's parent is still archived, the page moves to the top level.
    public func restore(_ id: String) throws {
        try ws.write(.pages) { db in
            try db.run(PageStore.subtreeCTE + "UPDATE pages SET is_archived = 0, updated_at = $now WHERE id IN (SELECT id FROM subtree)",
                       ["$id": id, "$now": SBClock.stamp()])
            try db.run("""
                UPDATE pages SET parent_id = NULL
                WHERE id = $id AND parent_id IN (SELECT id FROM pages WHERE is_archived = 1)
                """, ["$id": id])
        }
    }

    public func deletionImpact(_ id: String) throws -> (pages: Int, linkedTasks: Int) {
        try ws.read { db in
            let pages = try db.scalarInt(PageStore.subtreeCTE + "SELECT count(*) FROM subtree JOIN pages USING (id)", ["$id": id])
            let tasks = try db.scalarInt(PageStore.subtreeCTE + "SELECT count(*) FROM tasks WHERE page_id IN (SELECT id FROM subtree)", ["$id": id])
            return (Int(pages), Int(tasks))
        }
    }

    /// Deletes the page and all descendants. Linked tasks are kept and unlinked (FK ON DELETE SET NULL).
    public func delete(_ id: String) throws {
        try ws.write([.pages, .tasks]) { db in
            if try db.run("DELETE FROM pages WHERE id = $id", ["$id": id]) == 0 {
                throw SlothBossError("The page no longer exists.")
            }
        }
    }

    /// Moves a page under a new parent (nil = top level). Rejects cycles.
    public func move(_ id: String, to newParentId: String?) throws {
        try ws.write(.pages) { db in
            if newParentId == id { throw SlothBossError("A page cannot be its own parent.") }
            do {
                let order = try db.scalarDouble("SELECT coalesce(max(sort_order), 0) + 1 FROM pages WHERE parent_id IS $p", ["$p": newParentId])
                let n = try db.run("UPDATE pages SET parent_id = $p, sort_order = $o, updated_at = $now WHERE id = $id",
                                   ["$p": newParentId, "$o": order, "$now": SBClock.stamp(), "$id": id])
                if n == 0 { throw SlothBossError("The page no longer exists.") }
            } catch let e as SQLiteError where e.message.contains("page hierarchy cycle") {
                throw SlothBossError("A page cannot be moved inside one of its own subpages.")
            }
        }
    }

    public func backlinks(_ id: String) throws -> [PageSummary] {
        try ws.read { db in
            try db.query("""
                SELECT \(PageStore.prefixedSummaryColumns)
                FROM page_links l JOIN pages p ON p.id = l.source_page_id
                WHERE l.target_page_id = $id AND p.is_archived = 0 ORDER BY p.title
                """, ["$id": id], PageStore.mapSummary)
        }
    }

    public func outgoingLinks(_ id: String) throws -> [PageSummary] {
        try ws.read { db in
            try db.query("""
                SELECT \(PageStore.prefixedSummaryColumns)
                FROM page_links l JOIN pages p ON p.id = l.target_page_id
                WHERE l.source_page_id = $id ORDER BY p.title
                """, ["$id": id], PageStore.mapSummary)
        }
    }
}
