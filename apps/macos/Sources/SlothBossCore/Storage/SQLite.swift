import Foundation
import SQLite3

/// SQLITE_TRANSIENT is a C macro and is not imported into Swift: it tells SQLite to copy bound data.
let SQLITE_TRANSIENT = unsafeBitCast(-1, to: sqlite3_destructor_type.self)

public struct SQLiteError: Error, LocalizedError, CustomStringConvertible, Sendable {
    public let code: Int32
    public let message: String
    public var errorDescription: String? { message }
    public var description: String { "SQLite error \(code): \(message)" }
}

/// A value bound to a statement parameter.
public enum SQLValue: Sendable, Equatable {
    case null
    case int(Int64)
    case double(Double)
    case text(String)
}

/// Types that can be bound as statement parameters. Values are always bound, never interpolated.
public protocol SQLBindable {
    var sqlValue: SQLValue { get }
}

extension SQLValue: SQLBindable { public var sqlValue: SQLValue { self } }
extension String: SQLBindable { public var sqlValue: SQLValue { .text(self) } }
extension Int: SQLBindable { public var sqlValue: SQLValue { .int(Int64(self)) } }
extension Int64: SQLBindable { public var sqlValue: SQLValue { .int(self) } }
extension Double: SQLBindable { public var sqlValue: SQLValue { .double(self) } }
extension Bool: SQLBindable { public var sqlValue: SQLValue { .int(self ? 1 : 0) } }
extension Optional: SQLBindable where Wrapped: SQLBindable {
    public var sqlValue: SQLValue {
        switch self {
        case .none: return .null
        case .some(let v): return v.sqlValue
        }
    }
}

/// Named parameters, keys including the `$` prefix exactly as written in the SQL.
public typealias SQLParams = [String: any SQLBindable]

/// Read access to the current result row.
public struct SQLRow {
    let stmt: OpaquePointer

    public func isNull(_ i: Int32) -> Bool { sqlite3_column_type(stmt, i) == SQLITE_NULL }

    public func string(_ i: Int32) -> String {
        guard let p = sqlite3_column_text(stmt, i) else { return "" }
        let n = Int(sqlite3_column_bytes(stmt, i))
        return String(decoding: UnsafeBufferPointer(start: p, count: n), as: UTF8.self)
    }

    public func optionalString(_ i: Int32) -> String? { isNull(i) ? nil : string(i) }
    public func int64(_ i: Int32) -> Int64 { sqlite3_column_int64(stmt, i) }
    public func int(_ i: Int32) -> Int { Int(sqlite3_column_int64(stmt, i)) }
    public func double(_ i: Int32) -> Double { sqlite3_column_double(stmt, i) }
    public func bool(_ i: Int32) -> Bool { sqlite3_column_int64(stmt, i) == 1 }

    public func value(_ i: Int32) -> SQLValue {
        switch sqlite3_column_type(stmt, i) {
        case SQLITE_NULL: return .null
        case SQLITE_INTEGER: return .int(sqlite3_column_int64(stmt, i))
        case SQLITE_FLOAT: return .double(sqlite3_column_double(stmt, i))
        default: return .text(string(i))
        }
    }
}

/// A single SQLite connection. Not thread-safe by itself: `Workspace` serializes all access.
public final class SQLiteDatabase {
    private(set) var handle: OpaquePointer?

    public init(path: String) throws {
        var db: OpaquePointer?
        let flags = SQLITE_OPEN_READWRITE | SQLITE_OPEN_CREATE | SQLITE_OPEN_FULLMUTEX
        let rc = sqlite3_open_v2(path, &db, flags, nil)
        if rc != SQLITE_OK {
            let message = db.map { String(cString: sqlite3_errmsg($0)) } ?? "unable to open database"
            if let db { sqlite3_close_v2(db) }
            throw SQLiteError(code: rc, message: message)
        }
        handle = db
        sqlite3_extended_result_codes(db, 1)
    }

    deinit { close() }

    public func close() {
        if let h = handle {
            sqlite3_close_v2(h)
            handle = nil
        }
    }

    private func error(_ rc: Int32) -> SQLiteError {
        let message = handle.map { String(cString: sqlite3_errmsg($0)) } ?? "database is closed"
        return SQLiteError(code: rc, message: message)
    }

    private func openHandle() throws -> OpaquePointer {
        guard let h = handle else { throw SQLiteError(code: SQLITE_MISUSE, message: "The workspace database is closed.") }
        return h
    }

    /// Executes one or more statements without parameters (migrations, pragmas).
    public func executeScript(_ sql: String) throws {
        let h = try openHandle()
        var errmsg: UnsafeMutablePointer<CChar>?
        let rc = sqlite3_exec(h, sql, nil, nil, &errmsg)
        if rc != SQLITE_OK {
            let message = errmsg.map { String(cString: $0) } ?? String(cString: sqlite3_errmsg(h))
            sqlite3_free(errmsg)
            throw SQLiteError(code: rc, message: message)
        }
    }

    private func prepare(_ sql: String, _ params: SQLParams) throws -> OpaquePointer {
        let h = try openHandle()
        var stmt: OpaquePointer?
        let rc = sqlite3_prepare_v2(h, sql, -1, &stmt, nil)
        guard rc == SQLITE_OK, let stmt else { throw error(rc) }
        do {
            try bind(stmt, params)
        } catch {
            sqlite3_finalize(stmt)
            throw error
        }
        return stmt
    }

    private func bind(_ stmt: OpaquePointer, _ params: SQLParams) throws {
        let count = sqlite3_bind_parameter_count(stmt)
        var used = 0
        if count > 0 {
            for i in 1...count {
                guard let cname = sqlite3_bind_parameter_name(stmt, i) else {
                    throw SQLiteError(code: SQLITE_MISUSE, message: "Positional parameters are not supported.")
                }
                let name = String(cString: cname)
                guard let value = params[name] else {
                    throw SQLiteError(code: SQLITE_MISUSE, message: "Missing value for parameter \(name).")
                }
                used += 1
                let rc: Int32
                switch value.sqlValue {
                case .null: rc = sqlite3_bind_null(stmt, i)
                case .int(let v): rc = sqlite3_bind_int64(stmt, i, v)
                case .double(let v): rc = sqlite3_bind_double(stmt, i, v)
                case .text(let s):
                    let length = Int32(s.utf8.count)
                    rc = s.withCString { sqlite3_bind_text(stmt, i, $0, length, SQLITE_TRANSIENT) }
                }
                if rc != SQLITE_OK { throw error(rc) }
            }
        }
        if used != params.count {
            let unknown = params.keys.filter { sqlite3_bind_parameter_index(stmt, $0) == 0 }
            throw SQLiteError(code: SQLITE_MISUSE, message: "Unknown parameter(s): \(unknown.sorted().joined(separator: ", ")).")
        }
    }

    /// Runs a statement; returns the number of changed rows.
    @discardableResult
    public func run(_ sql: String, _ params: SQLParams = [:]) throws -> Int {
        let stmt = try prepare(sql, params)
        defer { sqlite3_finalize(stmt) }
        while true {
            let rc = sqlite3_step(stmt)
            if rc == SQLITE_DONE { break }
            if rc == SQLITE_ROW { continue }
            throw error(rc)
        }
        return Int(sqlite3_changes(handle))
    }

    public func query<T>(_ sql: String, _ params: SQLParams = [:], _ map: (SQLRow) throws -> T) throws -> [T] {
        let stmt = try prepare(sql, params)
        defer { sqlite3_finalize(stmt) }
        var results: [T] = []
        while true {
            let rc = sqlite3_step(stmt)
            if rc == SQLITE_DONE { break }
            guard rc == SQLITE_ROW else { throw error(rc) }
            results.append(try map(SQLRow(stmt: stmt)))
        }
        return results
    }

    /// First column of the first row, or `.null` when there are no rows.
    public func scalar(_ sql: String, _ params: SQLParams = [:]) throws -> SQLValue {
        try query(sql, params) { $0.value(0) }.first ?? .null
    }

    public func scalarString(_ sql: String, _ params: SQLParams = [:]) throws -> String? {
        if case .text(let s) = try scalar(sql, params) { return s }
        return nil
    }

    public func scalarInt(_ sql: String, _ params: SQLParams = [:]) throws -> Int64 {
        switch try scalar(sql, params) {
        case .int(let i): return i
        case .double(let d): return Int64(d)
        case .text(let s): return Int64(s) ?? 0
        case .null: return 0
        }
    }

    public func scalarDouble(_ sql: String, _ params: SQLParams = [:]) throws -> Double {
        switch try scalar(sql, params) {
        case .int(let i): return Double(i)
        case .double(let d): return d
        case .text(let s): return Double(s) ?? 0
        case .null: return 0
        }
    }

    /// True if the query returns at least one row.
    public func exists(_ sql: String, _ params: SQLParams = [:]) throws -> Bool {
        try !query(sql, params) { _ in true }.isEmpty
    }

    public var isInTransaction: Bool {
        guard let h = handle else { return false }
        return sqlite3_get_autocommit(h) == 0
    }
}
