import Foundation

/// A small JSON value type with a strict parser and deterministic serializer.
///
/// Used instead of JSONSerialization so that (a) integers and booleans are never confused,
/// (b) object key order is preserved, and (c) output bytes are fully under our control
/// (canonical page JSON must match the shared fixtures exactly).
public enum JSON: Equatable, Sendable {
    case null
    case bool(Bool)
    /// A number written without fraction or exponent that fits in Int64.
    case int(Int64)
    case double(Double)
    case string(String)
    case array([JSON])
    case object(JSONObject)

    // MARK: accessors

    public var stringValue: String? { if case .string(let s) = self { return s } else { return nil } }
    public var boolValue: Bool? { if case .bool(let b) = self { return b } else { return nil } }
    public var intValue: Int64? { if case .int(let i) = self { return i } else { return nil } }
    public var doubleValue: Double? {
        switch self {
        case .int(let i): return Double(i)
        case .double(let d): return d
        default: return nil
        }
    }
    public var arrayValue: [JSON]? { if case .array(let a) = self { return a } else { return nil } }
    public var objectValue: JSONObject? { if case .object(let o) = self { return o } else { return nil } }
    public var isNull: Bool { if case .null = self { return true } else { return false } }

    public subscript(key: String) -> JSON? { objectValue?[key] }

    // MARK: parsing

    public struct ParseError: Error, CustomStringConvertible, Sendable {
        public let message: String
        public let offset: Int
        public var description: String { "\(message) at byte \(offset)" }
    }

    public static func parse(_ text: String) throws -> JSON {
        try parse(Data(text.utf8))
    }

    public static func parse(_ data: Data) throws -> JSON {
        var parser = Parser(bytes: [UInt8](data))
        return try parser.parseDocument()
    }

    // MARK: serialization

    /// Compact serialization (no whitespace). Non-ASCII characters are written as-is.
    public func serialized() -> String {
        var out = ""
        write(into: &out, indent: nil, level: 0)
        return out
    }

    /// Indented serialization with two-space indentation.
    public func prettySerialized() -> String {
        var out = ""
        write(into: &out, indent: "  ", level: 0)
        return out
    }

    private func write(into out: inout String, indent: String?, level: Int) {
        switch self {
        case .null: out += "null"
        case .bool(let b): out += b ? "true" : "false"
        case .int(let i): out += String(i)
        case .double(let d): out += JSON.formatDouble(d)
        case .string(let s): JSON.writeString(s, into: &out)
        case .array(let items):
            if items.isEmpty { out += "[]"; return }
            out += "["
            for (i, item) in items.enumerated() {
                if i > 0 { out += "," }
                if let indent { out += "\n" + String(repeating: indent, count: level + 1) }
                item.write(into: &out, indent: indent, level: level + 1)
            }
            if let indent { out += "\n" + String(repeating: indent, count: level) }
            out += "]"
        case .object(let obj):
            if obj.isEmpty { out += "{}"; return }
            out += "{"
            for (i, (key, value)) in obj.pairs.enumerated() {
                if i > 0 { out += "," }
                if let indent { out += "\n" + String(repeating: indent, count: level + 1) }
                JSON.writeString(key, into: &out)
                out += indent == nil ? ":" : ": "
                value.write(into: &out, indent: indent, level: level + 1)
            }
            if let indent { out += "\n" + String(repeating: indent, count: level) }
            out += "}"
        }
    }

    static func formatDouble(_ d: Double) -> String {
        guard d.isFinite else { return "null" }
        if d == d.rounded(), abs(d) < 1e15 { return String(Int64(d)) }
        return "\(d)"
    }

    static func writeString(_ s: String, into out: inout String) {
        out += "\""
        for scalar in s.unicodeScalars {
            switch scalar {
            case "\"": out += "\\\""
            case "\\": out += "\\\\"
            case "\n": out += "\\n"
            case "\r": out += "\\r"
            case "\t": out += "\\t"
            case "\u{8}": out += "\\b"
            case "\u{C}": out += "\\f"
            default:
                if scalar.value < 0x20 {
                    out += String(format: "\\u%04X", scalar.value)
                } else {
                    out.unicodeScalars.append(scalar)
                }
            }
        }
        out += "\""
    }
}

/// An insertion-ordered JSON object.
public struct JSONObject: Equatable, Sendable {
    public private(set) var pairs: [(String, JSON)] = []
    private var index: [String: Int] = [:]

    public init() {}

    public init(_ pairs: [(String, JSON)]) {
        for (k, v) in pairs { self[k] = v }
    }

    public var isEmpty: Bool { pairs.isEmpty }
    public var keys: [String] { pairs.map { $0.0 } }
    public func contains(_ key: String) -> Bool { index[key] != nil }

    public subscript(key: String) -> JSON? {
        get { index[key].map { pairs[$0].1 } }
        set {
            if let i = index[key] {
                if let newValue { pairs[i].1 = newValue } else {
                    pairs.remove(at: i)
                    index = [:]
                    for (j, p) in pairs.enumerated() { index[p.0] = j }
                }
            } else if let newValue {
                index[key] = pairs.count
                pairs.append((key, newValue))
            }
        }
    }

    public static func == (a: JSONObject, b: JSONObject) -> Bool {
        guard a.pairs.count == b.pairs.count else { return false }
        for (k, v) in a.pairs where b[k] != v { return false }
        return true
    }
}

extension JSON: ExpressibleByStringLiteral, ExpressibleByBooleanLiteral, ExpressibleByIntegerLiteral {
    public init(stringLiteral value: String) { self = .string(value) }
    public init(booleanLiteral value: Bool) { self = .bool(value) }
    public init(integerLiteral value: Int) { self = .int(Int64(value)) }

    /// `.string(s)` or `.null` when nil.
    public static func optional(_ s: String?) -> JSON { s.map { .string($0) } ?? .null }
}

// MARK: - Parser

private struct Parser {
    let bytes: [UInt8]
    var pos = 0
    var depth = 0
    static let maxDepth = 256

    init(bytes: [UInt8]) {
        self.bytes = bytes
        // Skip a UTF-8 byte order mark.
        if bytes.count >= 3, bytes[0] == 0xEF, bytes[1] == 0xBB, bytes[2] == 0xBF { pos = 3 }
    }

    func fail(_ message: String) -> JSON.ParseError { JSON.ParseError(message: message, offset: pos) }

    mutating func parseDocument() throws -> JSON {
        skipWhitespace()
        let value = try parseValue()
        skipWhitespace()
        if pos != bytes.count { throw fail("Unexpected trailing characters") }
        return value
    }

    mutating func skipWhitespace() {
        while pos < bytes.count {
            let b = bytes[pos]
            if b == 0x20 || b == 0x09 || b == 0x0A || b == 0x0D { pos += 1 } else { break }
        }
    }

    mutating func parseValue() throws -> JSON {
        guard pos < bytes.count else { throw fail("Unexpected end of input") }
        switch bytes[pos] {
        case UInt8(ascii: "{"): return try parseObject()
        case UInt8(ascii: "["): return try parseArray()
        case UInt8(ascii: "\""): return .string(try parseString())
        case UInt8(ascii: "t"): try expectLiteral("true"); return .bool(true)
        case UInt8(ascii: "f"): try expectLiteral("false"); return .bool(false)
        case UInt8(ascii: "n"): try expectLiteral("null"); return .null
        case UInt8(ascii: "-"), UInt8(ascii: "0")...UInt8(ascii: "9"): return try parseNumber()
        default: throw fail("Unexpected character")
        }
    }

    mutating func expectLiteral(_ word: String) throws {
        let w = Array(word.utf8)
        guard pos + w.count <= bytes.count, Array(bytes[pos..<pos + w.count]) == w else { throw fail("Invalid literal") }
        pos += w.count
    }

    mutating func parseObject() throws -> JSON {
        depth += 1
        if depth > Parser.maxDepth { throw fail("Nesting too deep") }
        defer { depth -= 1 }
        pos += 1 // {
        var obj = JSONObject()
        skipWhitespace()
        if pos < bytes.count, bytes[pos] == UInt8(ascii: "}") { pos += 1; return .object(obj) }
        while true {
            skipWhitespace()
            guard pos < bytes.count, bytes[pos] == UInt8(ascii: "\"") else { throw fail("Expected property name") }
            let key = try parseString()
            if obj.contains(key) { throw fail("Duplicate property '\(key)'") }
            skipWhitespace()
            guard pos < bytes.count, bytes[pos] == UInt8(ascii: ":") else { throw fail("Expected ':'") }
            pos += 1
            skipWhitespace()
            obj[key] = try parseValue()
            skipWhitespace()
            guard pos < bytes.count else { throw fail("Unexpected end of input") }
            if bytes[pos] == UInt8(ascii: ",") { pos += 1; continue }
            if bytes[pos] == UInt8(ascii: "}") { pos += 1; return .object(obj) }
            throw fail("Expected ',' or '}'")
        }
    }

    mutating func parseArray() throws -> JSON {
        depth += 1
        if depth > Parser.maxDepth { throw fail("Nesting too deep") }
        defer { depth -= 1 }
        pos += 1 // [
        var items: [JSON] = []
        skipWhitespace()
        if pos < bytes.count, bytes[pos] == UInt8(ascii: "]") { pos += 1; return .array(items) }
        while true {
            skipWhitespace()
            items.append(try parseValue())
            skipWhitespace()
            guard pos < bytes.count else { throw fail("Unexpected end of input") }
            if bytes[pos] == UInt8(ascii: ",") { pos += 1; continue }
            if bytes[pos] == UInt8(ascii: "]") { pos += 1; return .array(items) }
            throw fail("Expected ',' or ']'")
        }
    }

    mutating func parseHex4() throws -> UInt32 {
        guard pos + 4 <= bytes.count else { throw fail("Invalid \\u escape") }
        var v: UInt32 = 0
        for _ in 0..<4 {
            let b = bytes[pos]
            let d: UInt32
            switch b {
            case 48...57: d = UInt32(b - 48)
            case 65...70: d = UInt32(b - 55)
            case 97...102: d = UInt32(b - 87)
            default: throw fail("Invalid \\u escape")
            }
            v = v * 16 + d
            pos += 1
        }
        return v
    }

    mutating func parseString() throws -> String {
        pos += 1 // opening quote
        var buffer: [UInt8] = []
        var scalarsOut = String.UnicodeScalarView()
        func flush(_ buffer: inout [UInt8], into out: inout String.UnicodeScalarView) {
            if !buffer.isEmpty {
                out.append(contentsOf: String(decoding: buffer, as: UTF8.self).unicodeScalars)
                buffer.removeAll(keepingCapacity: true)
            }
        }
        while true {
            guard pos < bytes.count else { throw fail("Unterminated string") }
            let b = bytes[pos]
            if b == UInt8(ascii: "\"") {
                pos += 1
                flush(&buffer, into: &scalarsOut)
                return String(scalarsOut)
            }
            if b < 0x20 { throw fail("Control character in string") }
            if b == UInt8(ascii: "\\") {
                pos += 1
                guard pos < bytes.count else { throw fail("Unterminated escape") }
                let e = bytes[pos]
                pos += 1
                switch e {
                case UInt8(ascii: "\""): buffer.append(0x22)
                case UInt8(ascii: "\\"): buffer.append(0x5C)
                case UInt8(ascii: "/"): buffer.append(0x2F)
                case UInt8(ascii: "b"): buffer.append(0x08)
                case UInt8(ascii: "f"): buffer.append(0x0C)
                case UInt8(ascii: "n"): buffer.append(0x0A)
                case UInt8(ascii: "r"): buffer.append(0x0D)
                case UInt8(ascii: "t"): buffer.append(0x09)
                case UInt8(ascii: "u"):
                    flush(&buffer, into: &scalarsOut)
                    var code = try parseHex4()
                    if (0xD800...0xDBFF).contains(code) {
                        // High surrogate: combine with a following low surrogate if present.
                        if pos + 6 <= bytes.count, bytes[pos] == UInt8(ascii: "\\"), bytes[pos + 1] == UInt8(ascii: "u") {
                            let save = pos
                            pos += 2
                            let low = try parseHex4()
                            if (0xDC00...0xDFFF).contains(low) {
                                code = 0x10000 + ((code - 0xD800) << 10) + (low - 0xDC00)
                            } else {
                                pos = save
                                code = 0xFFFD
                            }
                        } else {
                            code = 0xFFFD
                        }
                    } else if (0xDC00...0xDFFF).contains(code) {
                        code = 0xFFFD
                    }
                    scalarsOut.append(Unicode.Scalar(code) ?? "\u{FFFD}")
                default:
                    throw fail("Invalid escape")
                }
                continue
            }
            buffer.append(b)
            pos += 1
        }
    }

    mutating func parseNumber() throws -> JSON {
        let start = pos
        var isInteger = true
        if bytes[pos] == UInt8(ascii: "-") { pos += 1 }
        guard pos < bytes.count else { throw fail("Invalid number") }
        if bytes[pos] == UInt8(ascii: "0") {
            pos += 1
        } else if (49...57).contains(bytes[pos]) {
            while pos < bytes.count, (48...57).contains(bytes[pos]) { pos += 1 }
        } else {
            throw fail("Invalid number")
        }
        if pos < bytes.count, bytes[pos] == UInt8(ascii: ".") {
            isInteger = false
            pos += 1
            guard pos < bytes.count, (48...57).contains(bytes[pos]) else { throw fail("Invalid number") }
            while pos < bytes.count, (48...57).contains(bytes[pos]) { pos += 1 }
        }
        if pos < bytes.count, bytes[pos] == UInt8(ascii: "e") || bytes[pos] == UInt8(ascii: "E") {
            isInteger = false
            pos += 1
            if pos < bytes.count, bytes[pos] == UInt8(ascii: "+") || bytes[pos] == UInt8(ascii: "-") { pos += 1 }
            guard pos < bytes.count, (48...57).contains(bytes[pos]) else { throw fail("Invalid number") }
            while pos < bytes.count, (48...57).contains(bytes[pos]) { pos += 1 }
        }
        let text = String(decoding: bytes[start..<pos], as: UTF8.self)
        if isInteger, let i = Int64(text) { return .int(i) }
        guard let d = Double(text) else { throw fail("Invalid number") }
        return .double(d)
    }
}
