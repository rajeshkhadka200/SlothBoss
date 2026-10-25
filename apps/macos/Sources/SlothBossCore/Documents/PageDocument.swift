import Foundation

public enum BlockType: String, CaseIterable, Sendable {
    case paragraph, heading, bulleted, numbered, todo, quote, code
}

public struct TextSpan: Equatable, Sendable {
    public var text: String
    public var bold: Bool
    public var italic: Bool
    public var code: Bool
    public var link: String?

    public init(_ text: String = "", bold: Bool = false, italic: Bool = false, code: Bool = false, link: String? = nil) {
        self.text = text
        self.bold = bold
        self.italic = italic
        self.code = code
        self.link = link
    }

    public func sameFormat(_ other: TextSpan) -> Bool {
        bold == other.bold && italic == other.italic && code == other.code && link == other.link
    }
}

public struct Block: Equatable, Sendable {
    public var type: BlockType
    /// 1-3, headings only.
    public var level: Int
    /// Todo only.
    public var checked: Bool
    public var spans: [TextSpan]

    public init(_ type: BlockType = .paragraph, _ spans: [TextSpan] = [], level: Int? = nil, checked: Bool = false) {
        self.type = type
        self.spans = spans
        self.level = level ?? (type == .heading ? 1 : 0)
        self.checked = checked
    }

    public var text: String { spans.map(\.text).joined() }

    public static func paragraph(_ text: String) -> Block { Block(.paragraph, [TextSpan(text)]) }
    public static func heading(_ level: Int, _ text: String) -> Block { Block(.heading, [TextSpan(text)], level: level) }
}

/// Structured page content, persisted as pages.content_json (contracts/schemas/content.schema.json).
public struct PageDocument: Equatable, Sendable {
    public static let currentVersion = 1
    public static let emptyJSON = "{\"version\":1,\"blocks\":[]}"

    public var blocks: [Block]

    public init(_ blocks: [Block] = []) { self.blocks = blocks }

    /// Merges adjacent equally formatted spans, drops empty spans and unsafe links, clamps heading levels.
    public mutating func normalize() {
        for bi in blocks.indices {
            var block = blocks[bi]
            if block.type == .heading { block.level = min(max(block.level, 1), 3) } else { block.level = 0 }
            if block.type != .todo { block.checked = false }

            var merged: [TextSpan] = []
            for var span in block.spans {
                if let link = span.link, !Links.isAllowed(link) { span.link = nil }
                if block.type == .code {
                    span.bold = false; span.italic = false; span.code = false; span.link = nil
                } else {
                    span.text = span.text.newlinesToSpaces
                }
                if span.text.isEmpty { continue }
                if let last = merged.last, last.sameFormat(span) {
                    merged[merged.count - 1].text += span.text
                } else {
                    merged.append(span)
                }
            }
            block.spans = merged
            blocks[bi] = block
        }
    }

    public func normalized() -> PageDocument {
        var copy = self
        copy.normalize()
        return copy
    }

    // MARK: canonical JSON

    /// Canonical serialization: fixed key order, optional keys omitted when false/absent.
    public func toJSON() -> String { toJSONValue().serialized() }

    public func toJSONValue() -> JSON {
        .object(JSONObject([
            ("version", .int(Int64(PageDocument.currentVersion))),
            ("blocks", .array(blocks.map(PageDocument.blockJSON))),
        ]))
    }

    static func blockJSON(_ b: Block) -> JSON {
        var o = JSONObject()
        o["type"] = .string(b.type.rawValue)
        if b.type == .heading { o["level"] = .int(Int64(b.level)) }
        if b.type == .todo { o["checked"] = .bool(b.checked) }
        o["spans"] = .array(b.spans.map { s in
            var so = JSONObject()
            so["text"] = .string(s.text)
            if s.bold { so["bold"] = .bool(true) }
            if s.italic { so["italic"] = .bool(true) }
            if s.code { so["code"] = .bool(true) }
            if let link = s.link { so["link"] = .string(link) }
            return .object(so)
        })
        return .object(o)
    }

    public static func parse(_ json: String) throws -> PageDocument {
        let node: JSON
        do { node = try JSON.parse(json) } catch {
            throw SlothBossError.documentFormat("Content is not valid JSON: \(error)")
        }
        return try from(node)
    }

    /// Strict parse: unknown keys, types or versions are rejected.
    public static func from(_ node: JSON?) throws -> PageDocument {
        guard let root = node?.objectValue else { throw SlothBossError.documentFormat("Content must be a JSON object.") }
        try rejectUnknownKeys(root, "content", ["version", "blocks"])
        guard let version = root["version"]?.intValue else {
            throw SlothBossError.documentFormat("Content is missing an integer 'version'.")
        }
        if version != Int64(currentVersion) {
            throw SlothBossError.documentFormat("Unsupported content version \(version); this build supports version \(currentVersion).")
        }
        guard let blocks = root["blocks"]?.arrayValue else { throw SlothBossError.documentFormat("Content is missing 'blocks'.") }

        var doc = PageDocument()
        for item in blocks {
            guard let bo = item.objectValue else { throw SlothBossError.documentFormat("Each block must be an object.") }
            try rejectUnknownKeys(bo, "block", ["type", "level", "checked", "spans"])
            guard let typeName = try getString(bo, "type") else { throw SlothBossError.documentFormat("Block is missing 'type'.") }
            guard let type = BlockType(rawValue: typeName) else { throw SlothBossError.documentFormat("Unknown block type '\(typeName)'.") }
            var block = Block(type)
            if type == .heading {
                guard let level = bo["level"]?.intValue, (1...3).contains(level) else {
                    throw SlothBossError.documentFormat("Heading level must be 1, 2 or 3.")
                }
                block.level = Int(level)
            }
            if type == .todo { block.checked = try getBool(bo, "checked") }
            guard let spans = bo["spans"]?.arrayValue else { throw SlothBossError.documentFormat("Block is missing 'spans'.") }
            for sitem in spans {
                guard let so = sitem.objectValue else { throw SlothBossError.documentFormat("Each span must be an object.") }
                try rejectUnknownKeys(so, "span", ["text", "bold", "italic", "code", "link"])
                guard let text = try getString(so, "text") else { throw SlothBossError.documentFormat("Span is missing 'text'.") }
                block.spans.append(TextSpan(text, bold: try getBool(so, "bold"), italic: try getBool(so, "italic"),
                                        code: try getBool(so, "code"), link: try getString(so, "link")))
            }
            doc.blocks.append(block)
        }
        doc.normalize()
        return doc
    }

    private static func rejectUnknownKeys(_ o: JSONObject, _ what: String, _ allowed: Set<String>) throws {
        for key in o.keys where !allowed.contains(key) {
            throw SlothBossError.documentFormat("Unknown \(what) property '\(key)'.")
        }
    }

    /// Missing or JSON null -> nil (as in the reference); any other non-string -> error.
    private static func getString(_ o: JSONObject, _ key: String) throws -> String? {
        guard let v = o[key], !v.isNull else { return nil }
        guard let s = v.stringValue else { throw SlothBossError.documentFormat("Property '\(key)' must be a string.") }
        return s
    }

    private static func getBool(_ o: JSONObject, _ key: String) throws -> Bool {
        guard let v = o[key], !v.isNull else { return false }
        guard let b = v.boolValue else { throw SlothBossError.documentFormat("Property '\(key)' must be a boolean.") }
        return b
    }

    // MARK: projections

    /// Plain-text projection stored in pages.content_text: one line per non-empty block.
    public func plainText() -> String {
        blocks.map(\.text).filter { !$0.isEmpty }.joined(separator: "\n")
    }

    /// Distinct page ids referenced via slothboss://page/ links, in order of first appearance.
    public func pageReferences() -> [String] {
        var seen = Set<String>()
        var result: [String] = []
        for block in blocks {
            for span in block.spans {
                if let id = Links.parsePageLink(span.link), seen.insert(id).inserted { result.append(id) }
            }
        }
        return result
    }
}

public enum Links {
    public static let pagePrefix = "slothboss://page/"

    public static func forPage(_ pageId: String) -> String { pagePrefix + pageId }

    public static func parsePageLink(_ url: String?) -> String? {
        guard let url, url.lowercased().hasPrefix(pagePrefix) else { return nil }
        let id = String(url.dropFirst(pagePrefix.count)).lowercased()
        return Ids.isValid(id) ? id : nil
    }

    /// Only web, mail and internal page links are kept; javascript:, file: and others are dropped.
    public static func isAllowed(_ url: String) -> Bool {
        if parsePageLink(url) != nil { return true }
        let trimmed = url.trimmed
        guard !trimmed.isEmpty, let parsed = URL(string: trimmed), let scheme = parsed.scheme?.lowercased() else { return false }
        switch scheme {
        case "http", "https":
            return !(parsed.host ?? "").isEmpty
        case "mailto":
            return trimmed.count > "mailto:".count
        default:
            return false
        }
    }
}
