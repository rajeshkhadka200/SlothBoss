import Foundation

/// One retrieved excerpt. `id` (S1, S2, …) is the only identifier the model ever sees.
public struct Source: Equatable, Hashable, Identifiable, Sendable {
    public let id: String
    public let kind: String
    public let refId: String
    public let title: String
    public let excerpt: String
    public let pageId: String?

    public init(id: String, kind: String, refId: String, title: String, excerpt: String, pageId: String?) {
        self.id = id
        self.kind = kind
        self.refId = refId
        self.title = title
        self.excerpt = excerpt
        self.pageId = pageId
    }

    public var isPage: Bool { kind == "page" }
    public var displayTitle: String { title.isBlank ? "Untitled" : title }
}

public struct Evidence: Equatable, Sendable {
    public let sources: [Source]
    public let truncated: Bool

    public init(sources: [Source], truncated: Bool) {
        self.sources = sources
        self.truncated = truncated
    }

    public var isEmpty: Bool { sources.isEmpty }

    public func find(_ id: String) -> Source? {
        let wanted = id.uppercased()
        return sources.first { $0.id.uppercased() == wanted }
    }

    /// Renders the evidence block placed inside <evidence> tags in prompts.
    public func format() -> String {
        var out = ""
        for s in sources {
            if !out.isEmpty { out += "\n\n" }
            out += "[\(s.id)] \(s.isPage ? "Page" : "Task"): \"\(Retriever.sanitize(s.displayTitle))\"\n\(Retriever.sanitize(s.excerpt))"
        }
        return out
    }
}

/// Full-text retrieval for grounded answers: keyword extraction → FTS5 OR query ranked by bm25 →
/// per-page chunk scoring → budgeted evidence list. Specified in docs/ARCHITECTURE.md#retrieval.
public final class Retriever: @unchecked Sendable {
    public var charBudget = 6000
    public var maxSources = 8
    public var chunkChars = 700

    private let ws: Workspace

    public init(_ ws: Workspace) { self.ws = ws }

    static let stopWords: Set<String> = [
        "a", "an", "the", "and", "or", "but", "if", "then", "else", "of", "to", "in", "on", "at", "by", "for", "with", "about", "as", "into", "from",
        "is", "are", "was", "were", "be", "been", "being", "am", "do", "does", "did", "have", "has", "had", "can", "could", "should", "would",
        "will", "shall", "may", "might", "must", "i", "me", "my", "we", "our", "you", "your", "it", "its", "they", "them", "their", "this", "that",
        "these", "those", "what", "which", "who", "whom", "whose", "when", "where", "why", "how", "there", "here", "any", "all", "some", "each",
        "so", "not", "no", "yes", "just", "also", "than", "too", "very", "up", "out", "over", "again", "please", "tell", "show", "give", "find",
        "list", "summarize", "explain", "notes", "note", "workspace", "pages", "page", "based", "according", "use", "using", "make", "get",
    ]

    private static let evidenceTagRx = RegexPattern(#"</?\s*evidence\s*>"#, caseInsensitive: true)

    /// Prevents stored content from closing the evidence block early (prompt-injection hygiene).
    public static func sanitize(_ text: String) -> String { evidenceTagRx.replace(text, template: "(evidence)") }

    public static func keywords(_ question: String) -> [String] {
        Array(SearchService.tokenize(question).filter { t in
            let startsWithDigit = t.unicodeScalars.first.map { $0.properties.generalCategory == .decimalNumber } ?? false
            return (t.count >= 2 || startsWithDigit) && !stopWords.contains(t)
        }.prefix(12))
    }

    private struct Candidate {
        let kind: String
        let refId: String
        let title: String
        let excerpt: String
        let pageId: String?
    }

    public func retrieve(_ question: String, pinnedPageId: String? = nil) throws -> Evidence {
        let keywords = Retriever.keywords(question)
        var candidates: [Candidate] = []
        var seenPages = Set<String>()

        if let pinnedPageId, let pinned = try ws.pages.get(pinnedPageId) {
            seenPages.insert(pinned.id)
            for chunk in bestChunks(pinned.contentText, title: pinned.title, keywords: keywords, max: 3, requireMatch: false) {
                candidates.append(Candidate(kind: "page", refId: pinned.id, title: pinned.title, excerpt: chunk, pageId: pinned.id))
            }
        }

        if !keywords.isEmpty {
            for hit in try ws.search.queryAny(keywords, limit: 12) {
                if hit.isPage {
                    if !seenPages.insert(hit.refId).inserted { continue }
                    guard let page = try ws.pages.get(hit.refId), !page.isArchived else { continue }
                    for chunk in bestChunks(page.contentText, title: page.title, keywords: keywords, max: 2, requireMatch: true) {
                        candidates.append(Candidate(kind: "page", refId: page.id, title: page.title, excerpt: chunk, pageId: page.id))
                    }
                } else if let task = try ws.tasks.get(hit.refId) {
                    var excerpt = "Status: \(TaskState.label(task.status)); priority: \(task.priority)"
                    if let due = task.dueDate { excerpt += "; due " + due }
                    if let d = task.description, !d.isBlank { excerpt += "\n" + Retriever.truncate(d, chunkChars) }
                    candidates.append(Candidate(kind: "task", refId: task.id, title: task.title, excerpt: excerpt, pageId: task.pageId))
                }
            }
        }

        var result: [Source] = []
        var used = 0
        var truncated = false
        for c in candidates {
            if result.count >= maxSources || used + c.excerpt.count > charBudget { truncated = true; continue }
            used += c.excerpt.count
            result.append(Source(id: "S\(result.count + 1)", kind: c.kind, refId: c.refId, title: c.title, excerpt: c.excerpt, pageId: c.pageId))
        }
        return Evidence(sources: result, truncated: truncated)
    }

    /// The whole page as a single source, truncated to `maxChars` (summaries, task extraction).
    public func forPage(_ pageId: String, maxChars: Int = 12000) throws -> Evidence {
        guard let page = try ws.pages.get(pageId) else { throw SlothBossError("The page no longer exists.") }
        var text = page.contentText
        let truncated = text.count > maxChars
        if truncated { text = text.prefixString(maxChars) }
        return Evidence(sources: [Source(id: "S1", kind: "page", refId: page.id, title: page.title, excerpt: text, pageId: page.id)],
                        truncated: truncated)
    }

    func bestChunks(_ text: String, title: String, keywords: [String], max: Int, requireMatch: Bool) -> [String] {
        let chunks = chunk(text)
        if chunks.isEmpty { return [] }
        let titleLower = title.lowercased()
        let titleBonus: Double = keywords.contains(where: { titleLower.contains($0) }) ? 0.5 : 0
        let scored = chunks.enumerated().map { (chunk: $0.element, index: $0.offset, score: Retriever.score($0.element, keywords) + titleBonus) }
        var picked = Array(scored.filter { $0.score > 0 }
            .sorted { $0.score != $1.score ? $0.score > $1.score : $0.index < $1.index }
            .prefix(max))
        if picked.isEmpty && !requireMatch { picked = Array(scored.prefix(min(2, max))) }
        return picked.sorted { $0.index < $1.index }.map { $0.chunk }
    }

    static func score(_ chunk: String, _ keywords: [String]) -> Double {
        let lower = chunk.lowercased()
        var score = 0.0
        for k in keywords where !k.isEmpty {
            var count = 0
            var searchStart = lower.startIndex
            while count < 5, let r = lower.range(of: k, options: .literal, range: searchStart..<lower.endIndex) {
                count += 1
                searchStart = r.upperBound
            }
            if count > 0 { score += 1 + 0.25 * Double(count - 1) }
        }
        return score
    }

    func chunk(_ text: String) -> [String] {
        var chunks: [String] = []
        var current = ""
        for rawLine in text.components(separatedBy: "\n") {
            let line = rawLine.trimmed
            if line.isEmpty { continue }
            for piece in splitLong(line) {
                if !current.isEmpty && current.count + piece.count + 1 > chunkChars {
                    chunks.append(current)
                    current = ""
                }
                if !current.isEmpty { current += "\n" }
                current += piece
            }
        }
        if !current.isEmpty { chunks.append(current) }
        return chunks
    }

    private func splitLong(_ input: String) -> [String] {
        var pieces: [String] = []
        var line = Array(input)
        let breakers: Set<Character> = [".", "!", "?", ";", " "]
        while line.count > chunkChars {
            // Last break character at or before index chunkChars - 1.
            var cut = -1
            var j = chunkChars - 1
            while j >= 0 {
                if breakers.contains(line[j]) { cut = j; break }
                j -= 1
            }
            if cut < chunkChars / 2 { cut = chunkChars - 1 }
            pieces.append(String(line[0...cut]).trimmed)
            line = Array(String(line[(cut + 1)...]).trimmed)
        }
        if !line.isEmpty { pieces.append(String(line)) }
        return pieces
    }

    static func truncate(_ s: String, _ max: Int) -> String { s.count <= max ? s : s.prefixString(max) + "…" }
}
