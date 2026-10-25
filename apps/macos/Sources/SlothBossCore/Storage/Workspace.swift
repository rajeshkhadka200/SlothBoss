import Foundation

public struct WorkspaceChange: OptionSet, Sendable, Hashable {
    public let rawValue: Int
    public init(rawValue: Int) { self.rawValue = rawValue }

    public static let pages = WorkspaceChange(rawValue: 1)
    public static let tasks = WorkspaceChange(rawValue: 2)
    public static let settings = WorkspaceChange(rawValue: 4)
    public static let actions = WorkspaceChange(rawValue: 8)
    public static let all: WorkspaceChange = [.pages, .tasks, .settings, .actions]
}

/// An open SlothBoss workspace (one SQLite file). All database access goes through
/// `read` / `write`, which serialize access to the single connection with a lock so UI autosave
/// and background AI retrieval can't interleave statements.
public final class Workspace: @unchecked Sendable {
    /// Migration 002 adds the agent-run tables used by the Windows agent runtime; macOS creates them
    /// (so workspaces stay compatible) but has no agent UI yet.
    public static let schemaVersion = 2

    /// Posted (on the writing thread) after every committed write. `userInfo["change"]` holds the
    /// `WorkspaceChange` raw value.
    public static let didChangeNotification = Notification.Name("SlothBossWorkspaceDidChange")

    private let db: SQLiteDatabase
    private let lock = NSRecursiveLock()

    public let path: String
    public private(set) var pages: PageStore!
    public private(set) var tasks: TaskStore!
    public private(set) var settings: SettingsStore!
    public private(set) var search: SearchService!
    public private(set) var actions: ActionStore!

    /// Optional callback in addition to the notification.
    public var onChange: (@Sendable (WorkspaceChange) -> Void)?

    private init(path: String, db: SQLiteDatabase) {
        self.path = path
        self.db = db
        pages = PageStore(self)
        tasks = TaskStore(self)
        settings = SettingsStore(self)
        search = SearchService(self)
        actions = ActionStore(self)
    }

    /// Default location: ~/Library/Application Support/SlothBoss/workspace.db
    public static func defaultPath() throws -> String {
        let base = try FileManager.default.url(for: .applicationSupportDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
        return base.appendingPathComponent("SlothBoss", isDirectory: true).appendingPathComponent("workspace.db").path
    }

    /// Opens (creating if needed) and migrates the workspace. Use ":memory:" for an in-memory database.
    public static func open(path: String) throws -> Workspace {
        if path != ":memory:" {
            let dir = URL(fileURLWithPath: path).deletingLastPathComponent()
            try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        }
        let db = try SQLiteDatabase(path: path)
        do {
            try db.executeScript("PRAGMA foreign_keys = ON;")
            try db.executeScript("PRAGMA busy_timeout = 5000;")
            // Refuse newer schemas before anything (including the WAL switch) touches the file.
            let current = try db.scalarInt("PRAGMA user_version;")
            if current > Int64(schemaVersion) {
                throw SlothBossError(
                    "This workspace uses schema version \(current), which is newer than this app supports (\(schemaVersion)). " +
                    "Update SlothBoss to open it. The file was not modified.")
            }
            if path != ":memory:" { _ = try db.scalar("PRAGMA journal_mode = WAL;") }
            try migrate(db, current: current)
            return Workspace(path: path, db: db)
        } catch {
            db.close()
            throw error
        }
    }

    private static func migrate(_ db: SQLiteDatabase, current: Int64) throws {
        if try db.scalarInt("SELECT sqlite_compileoption_used('ENABLE_FTS5');") != 1 {
            // Some builds report options differently; probe directly before giving up.
            do {
                try db.executeScript("CREATE VIRTUAL TABLE temp.slothboss_fts5_probe USING fts5(x); DROP TABLE temp.slothboss_fts5_probe;")
            } catch {
                throw SlothBossError("This SQLite build lacks FTS5, which SlothBoss requires for search.")
            }
        }
        for migration in try ContractResources.migrations() where Int64(migration.version) > current {
            let sql = try ContractResources.read("schema/\(migration.name)")
            try db.executeScript("BEGIN IMMEDIATE;")
            do {
                try db.executeScript(sql)
                // PRAGMA cannot take bound parameters; the version is an Int parsed from the file name.
                try db.executeScript("PRAGMA user_version = \(migration.version);")
                try db.executeScript("COMMIT;")
            } catch {
                try? db.executeScript("ROLLBACK;")
                throw error
            }
        }
    }

    /// Runs `body` with exclusive access to the connection (no transaction).
    public func read<T>(_ body: (SQLiteDatabase) throws -> T) rethrows -> T {
        lock.lock()
        defer { lock.unlock() }
        return try body(db)
    }

    /// Runs `body` in a transaction; commits only if it returns normally, rolls back otherwise.
    /// Nested calls join the outer transaction.
    @discardableResult
    public func write<T>(_ change: WorkspaceChange, _ body: (SQLiteDatabase) throws -> T) throws -> T {
        let result: T
        lock.lock()
        do {
            if db.isInTransaction {
                result = try body(db)
                lock.unlock()
                return result
            }
            try db.executeScript("BEGIN IMMEDIATE;")
            do {
                result = try body(db)
                try db.executeScript("COMMIT;")
            } catch {
                try? db.executeScript("ROLLBACK;")
                throw error
            }
        } catch {
            lock.unlock()
            throw error
        }
        lock.unlock()
        notify(change)
        return result
    }

    private func notify(_ change: WorkspaceChange) {
        onChange?(change)
        NotificationCenter.default.post(name: Workspace.didChangeNotification, object: self, userInfo: ["change": change.rawValue])
    }

    /// Writes a consistent snapshot of the database to `destination` (must not exist).
    public func snapshot(to destination: String) throws {
        if FileManager.default.fileExists(atPath: destination) {
            throw SlothBossError("Refusing to overwrite existing file \(destination).")
        }
        try read { db in
            _ = try db.run("VACUUM INTO $path;", ["$path": destination])
        }
    }

    public func close() {
        lock.lock()
        db.close()
        lock.unlock()
    }
}
