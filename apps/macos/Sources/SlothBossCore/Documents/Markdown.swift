import Foundation

/// Markdown conversion. Export rules are specified in docs/DATA_MODEL.md and pinned by
/// contracts/fixtures/content; this is a port of the Windows reference (Markdown.cs).
public enum Markdown {
    private static func isListType(_ t: BlockType) -> Bool { t == .bulleted || t == .numbered || t == .todo }

    public static func exportPage(title: String, _ doc: PageDocument) -> String {
        let body = export(doc)
        let heading = "# " + escapeText(title.isBlank ? "Untitled" : title.trimmed)
        return body.isEmpty ? heading + "\n" : heading + "\n\n" + body + "\n"
    }

    public static func export(_ doc: PageDocument) -> String {
        var out = ""
        var prev: Block?
        var number = 0
        for block in doc.blocks {
            if block.text.trimmed.isEmpty && block.type != .code { prev = nil; number = 0; continue }
            if let p = prev { out += isListType(p.type) && isListType(block.type) ? "\n" : "\n\n" }
            number = block.type == .numbered ? number + 1 : 0
            switch block.type {
            case .heading: out += String(repeating: "#", count: block.level) + " " + inline(block.spans)
            case .bulleted: out += "- " + inline(block.spans)
            case .numbered: out += "\(number). " + inline(block.spans)
            case .todo: out += (block.checked ? "- [x] " : "- [ ] ") + inline(block.spans)
            case .quote: out += "> " + inline(block.spans)
            case .code: out += "```\n" + block.text + "\n```"
            case .paragraph: out += inline(block.spans)
            }
            prev = block
        }
        return out
    }

    private static func inline(_ spans: [TextSpan]) -> String {
        var out = ""
        for span in spans {
            let (lead, core, trail) = span.text.whitespaceSplit
            if core.isEmpty { out += span.text; continue }
            var rendered = span.code ? "`" + core.replacingOccurrences(of: "`", with: "'") + "`" : escapeText(core)
            if span.bold && span.italic { rendered = "***" + rendered + "***" }
            else if span.bold { rendered = "**" + rendered + "**" }
            else if span.italic { rendered = "*" + rendered + "*" }
            if let link = span.link {
                rendered = "[" + rendered + "](" + link.replacingOccurrences(of: ")", with: "%29").replacingOccurrences(of: " ", with: "%20") + ")"
            }
            out += lead + rendered + trail
        }
        return out
    }

    public static func escapeText(_ s: String) -> String {
        var out = ""
        for c in s {
            if c == "\\" || c == "*" || c == "_" || c == "`" || c == "[" || c == "]" { out.append("\\") }
            out.append(c)
        }
        return out
    }

    // MARK: parsing (used for AI drafted notes)

    private static let headingRx = RegexPattern(#"^(#{1,6})\s+(.*)$"#)
    private static let todoRx = RegexPattern(#"^[-*+]\s+\[([ xX])\]\s+(.*)$"#)
    private static let bulletRx = RegexPattern(#"^[-*+]\s+(.*)$"#)
    private static let numberRx = RegexPattern(#"^\d{1,9}[.)]\s+(.*)$"#)
    private static let quoteRx = RegexPattern(#"^>\s?(.*)$"#)

    public static func parse(_ markdown: String) -> PageDocument {
        var doc = PageDocument()
        let lines = markdown.replacingOccurrences(of: "\r\n", with: "\n")
            .replacingOccurrences(of: "\r", with: "\n")
            .components(separatedBy: "\n")
        var paragraph: [String] = []

        func flushParagraph() {
            if paragraph.isEmpty { return }
            doc.blocks.append(Block(.paragraph, parseInline(paragraph.joined(separator: " "))))
            paragraph.removeAll()
        }

        var i = 0
        while i < lines.count {
            let line = lines[i].trimmed
            if line.hasPrefix("```") {
                flushParagraph()
                var code: [String] = []
                i += 1
                while i < lines.count && !lines[i].trimmed.hasPrefix("```") {
                    code.append(lines[i])
                    i += 1
                }
                doc.blocks.append(Block(.code, [TextSpan(code.joined(separator: "\n"))]))
                i += 1
                continue
            }
            if line.isEmpty { flushParagraph(); i += 1; continue }

            if let m = headingRx.match(line) {
                flushParagraph()
                doc.blocks.append(Block(.heading, parseInline(m[2].trimmingEnd(["#", " "])), level: min(m[1].count, 3)))
            } else if let m = todoRx.match(line) {
                flushParagraph()
                doc.blocks.append(Block(.todo, parseInline(m[2]), checked: m[1] == "x" || m[1] == "X"))
            } else if let m = bulletRx.match(line) {
                flushParagraph()
                doc.blocks.append(Block(.bulleted, parseInline(m[1])))
            } else if let m = numberRx.match(line) {
                flushParagraph()
                doc.blocks.append(Block(.numbered, parseInline(m[1])))
            } else if let m = quoteRx.match(line) {
                flushParagraph()
                doc.blocks.append(Block(.quote, parseInline(m[1])))
            } else {
                paragraph.append(line)
            }
            i += 1
        }
        flushParagraph()
        doc.normalize()
        return doc
    }

    private static let escapable: Set<Character> = ["\\", "*", "_", "`", "[", "]", "(", ")", "#", ">", "-"]

    /// Parses **bold**, *italic* / _italic_, `code`, [text](url) and backslash escapes. No nesting beyond bold+italic.
    public static func parseInline(_ input: String) -> [TextSpan] {
        let s = Array(input)
        var spans: [TextSpan] = []
        var text = ""
        var bold = false
        var italic = false

        func flush(_ link: String? = nil) {
            if text.isEmpty { return }
            spans.append(TextSpan(text, bold: bold, italic: italic, link: link))
            text = ""
        }

        func indexOf(_ c: Character, from start: Int) -> Int? {
            var j = start
            while j < s.count {
                if s[j] == c { return j }
                j += 1
            }
            return nil
        }

        var i = 0
        while i < s.count {
            let c = s[i]
            if c == "\\" && i + 1 < s.count && escapable.contains(s[i + 1]) {
                text.append(s[i + 1])
                i += 2
                continue
            }
            if c == "`", let end = indexOf("`", from: i + 1) {
                flush()
                spans.append(TextSpan(String(s[(i + 1)..<end]), bold: bold, italic: italic, code: true))
                i = end + 1
                continue
            }
            if c == "[" {
                let close = findClosingBracket(s, i)
                if close > i && close + 1 < s.count && s[close + 1] == "(" {
                    if let paren = indexOf(")", from: close + 2) {
                        flush()
                        let url = String(s[(close + 2)..<paren]).trimmed
                        let inner = parseInline(String(s[(i + 1)..<close]))
                        let link = Links.isAllowed(url) ? url : nil
                        for var sp in inner {
                            sp.bold = sp.bold || bold
                            sp.italic = sp.italic || italic
                            sp.link = link
                            spans.append(sp)
                        }
                        i = paren + 1
                        continue
                    }
                }
            }
            if c == "*" || c == "_" {
                var run = 1
                while i + run < s.count && s[i + run] == c && run < 3 { run += 1 }
                // An underscore inside a word (snake_case) is literal.
                if c == "_" && i > 0 && isLetterOrDigit(s[i - 1]) && i + run < s.count && isLetterOrDigit(s[i + run]) {
                    text.append(String(s[i..<(i + run)]))
                    i += run
                    continue
                }
                flush()
                if run >= 2 { bold.toggle() }
                if run == 1 || run == 3 { italic.toggle() }
                i += run
                continue
            }
            text.append(c)
            i += 1
        }
        flush()
        return spans
    }

    private static func isLetterOrDigit(_ c: Character) -> Bool { c.isLetter || c.isNumber }

    private static func findClosingBracket(_ s: [Character], _ open: Int) -> Int {
        var depth = 0
        var j = open
        while j < s.count {
            if s[j] == "\\" { j += 2; continue }
            if s[j] == "[" { depth += 1 }
            else if s[j] == "]" {
                depth -= 1
                if depth == 0 { return j }
            }
            j += 1
        }
        return -1
    }
}
