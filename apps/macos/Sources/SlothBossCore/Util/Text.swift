import Foundation

// String helpers that mirror the .NET semantics the reference relies on.
// Note: .NET string lengths are UTF-16 code units; here lengths are Swift Characters.
// The results are identical for ordinary text.

extension String {
    /// Trims Unicode whitespace and newlines from both ends (like .NET `Trim()`).
    var trimmed: String { trimmingCharacters(in: .whitespacesAndNewlines) }

    /// Splits into (leading whitespace, core, trailing whitespace).
    var whitespaceSplit: (lead: String, core: String, trail: String) {
        let ws = CharacterSet.whitespacesAndNewlines
        let scalars = unicodeScalars
        var start = scalars.startIndex
        while start < scalars.endIndex, ws.contains(scalars[start]) { start = scalars.index(after: start) }
        var end = scalars.endIndex
        while end > start, ws.contains(scalars[scalars.index(before: end)]) { end = scalars.index(before: end) }
        return (String(scalars[scalars.startIndex..<start]), String(scalars[start..<end]), String(scalars[end..<scalars.endIndex]))
    }

    /// Trims the given characters from both ends (like .NET `Trim(params char[])`).
    func trimming(_ chars: Set<Character>) -> String {
        var s = Substring(self)
        while let f = s.first, chars.contains(f) { s = s.dropFirst() }
        while let l = s.last, chars.contains(l) { s = s.dropLast() }
        return String(s)
    }

    func trimmingEnd(_ chars: Set<Character>) -> String {
        var s = Substring(self)
        while let l = s.last, chars.contains(l) { s = s.dropLast() }
        return String(s)
    }

    /// First `n` characters.
    func prefixString(_ n: Int) -> String { String(prefix(max(0, n))) }

    /// True when the string has no non-whitespace characters (like .NET `IsNullOrWhiteSpace`).
    var isBlank: Bool { trimmed.isEmpty }

    /// Replaces CR LF, CR and LF with a single space each (CR LF counts as one).
    var newlinesToSpaces: String {
        replacingOccurrences(of: "\r\n", with: " ")
            .replacingOccurrences(of: "\n", with: " ")
            .replacingOccurrences(of: "\r", with: " ")
    }
}

/// Thin wrapper over NSRegularExpression working on UTF-16 ranges, so behavior matches .NET regex.
struct RegexPattern: @unchecked Sendable {
    let re: NSRegularExpression

    init(_ pattern: String, caseInsensitive: Bool = false) {
        // Patterns are compile-time constants; a failure here is a programming error.
        re = try! NSRegularExpression(pattern: pattern, options: caseInsensitive ? [.caseInsensitive] : [])
    }

    /// Capture groups of the first match (index 0 = whole match); "" for groups that did not participate.
    func match(_ s: String) -> [String]? {
        let ns = s as NSString
        guard let m = re.firstMatch(in: s, options: [], range: NSRange(location: 0, length: ns.length)) else { return nil }
        return (0..<m.numberOfRanges).map { i in
            let r = m.range(at: i)
            return r.location == NSNotFound ? "" : ns.substring(with: r)
        }
    }

    func matches(_ s: String) -> [NSTextCheckingResult] {
        re.matches(in: s, options: [], range: NSRange(location: 0, length: (s as NSString).length))
    }

    func allMatchStrings(_ s: String) -> [String] {
        let ns = s as NSString
        return matches(s).map { ns.substring(with: $0.range) }
    }

    /// Replaces with a template ("$1" references groups).
    func replace(_ s: String, template: String) -> String {
        re.stringByReplacingMatches(in: s, options: [], range: NSRange(location: 0, length: (s as NSString).length), withTemplate: template)
    }

    /// Replaces each match with the closure result (closure receives the groups, index 0 = whole match).
    func replace(_ s: String, _ transform: ([String]) -> String) -> String {
        let ns = s as NSString
        var out = ""
        var last = 0
        for m in matches(s) {
            out += ns.substring(with: NSRange(location: last, length: m.range.location - last))
            let groups = (0..<m.numberOfRanges).map { i -> String in
                let r = m.range(at: i)
                return r.location == NSNotFound ? "" : ns.substring(with: r)
            }
            out += transform(groups)
            last = m.range.location + m.range.length
        }
        out += ns.substring(from: last)
        return out
    }
}
