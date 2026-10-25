import Foundation

public struct SearchHit: Equatable, Hashable, Identifiable, Sendable {
    /// Snippet match markers (wrap matched terms). UIs render the enclosed text emphasized.
    public static let matchStart: Character = "\u{2}"
    public static let matchEnd: Character = "\u{3}"

    public let kind: String
    public let refId: String
    public let title: String
    public let snippet: String
    public let rank: Double

    public var id: String { kind + ":" + refId }
    public var isPage: Bool { kind == "page" }
}

/// Local full-text search over the FTS5 index maintained by schema triggers.
public final class SearchService: @unchecked Sendable {
    private unowned let ws: Workspace
    init(_ ws: Workspace) { self.ws = ws }

    private static let tokenRx = RegexPattern(#"[\p{L}\p{N}]+"#)

    /// Lowercased distinct letter/digit runs, in order of first appearance.
    public static func tokenize(_ text: String) -> [String] {
        var seen = Set<String>()
        var result: [String] = []
        for token in tokenRx.allMatchStrings(text) {
            let lower = token.lowercased()
            if seen.insert(lower).inserted { result.append(lower) }
        }
        return result
    }

    /// Builds a safe FTS5 MATCH expression: each token is quoted (so user input can't inject FTS
    /// syntax) and prefix-matched. Returns nil when there is nothing to search for.
    public static func buildMatch(_ tokens: [String], any: Bool) -> String? {
        let parts = tokens.filter { !$0.isEmpty }.prefix(16).map { "\"" + $0.replacingOccurrences(of: "\"", with: "\"\"") + "\"*" }
        return parts.isEmpty ? nil : parts.joined(separator: any ? " OR " : " ")
    }

    /// All terms must match (prefix). Empty or symbol-only queries return no results.
    public func query(_ query: String, limit: Int = 40) throws -> [SearchHit] {
        try run(SearchService.buildMatch(SearchService.tokenize(query), any: false), limit: limit)
    }

    /// Any term may match; used for AI retrieval where recall matters more than precision.
    public func queryAny(_ terms: [String], limit: Int = 20) throws -> [SearchHit] {
        try run(SearchService.buildMatch(terms, any: true), limit: limit)
    }

    private func run(_ match: String?, limit: Int) throws -> [SearchHit] {
        guard let match else { return [] }
        return try ws.read { db in
            try db.query("""
                SELECT s.kind, s.ref_id,
                       coalesce(p.title, t.title, ''),
                       snippet(search_index, 3, char(2), char(3), '…', 14),
                       bm25(search_index, 0.0, 0.0, 6.0, 1.0) AS rank
                FROM search_index s
                LEFT JOIN pages p ON s.kind = 'page' AND p.id = s.ref_id
                LEFT JOIN tasks t ON s.kind = 'task' AND t.id = s.ref_id
                WHERE search_index MATCH $q
                ORDER BY rank
                LIMIT $n
                """, ["$q": match, "$n": limit]) { r in
                SearchHit(kind: r.string(0), refId: r.string(1), title: r.string(2), snippet: r.string(3), rank: r.double(4))
            }
        }
    }
}
