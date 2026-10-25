import AppKit
import SlothBossCore

/// Visual conventions of the page editor. The converter below reads these same attributes back,
/// so every style here must stay detectable:
/// - headings: bold system font at 26 / 20 / 17 pt
/// - bulleted / numbered: paragraph style with an NSTextList; the marker ("\t•\t" or "\t3.\t") is
///   part of the text, as AppKit (TextKit 1) list paragraphs expect
/// - todo: paragraph starting with "☐ " or "☑ "
/// - quote: indented paragraph in the body font; code: indented paragraph in a monospaced font
/// - bold / italic: font traits; inline code: monospaced font; links: the `.link` attribute
enum EditorStyle {
    static let bodySize: CGFloat = 14
    static let codeSize: CGFloat = 13
    static let indent: CGFloat = 24
    static let listIndent: CGFloat = 30

    static let uncheckedBox = "☐ "
    static let checkedBox = "☑ "

    static func headingSize(_ level: Int) -> CGFloat { [26, 20, 17][min(max(level, 1), 3) - 1] }
    static var bodyFont: NSFont { NSFont.systemFont(ofSize: bodySize) }
    static var monoFont: NSFont { NSFont.monospacedSystemFont(ofSize: codeSize, weight: .regular) }
    static func headingFont(_ level: Int) -> NSFont { NSFont.boldSystemFont(ofSize: headingSize(level)) }

    /// Heading level for a font size, or nil for body text.
    static func headingLevel(forSize size: CGFloat) -> Int? {
        if size >= 23 { return 1 }
        if size >= 18.5 { return 2 }
        if size >= 16 { return 3 }
        return nil
    }

    static func isMonospaced(_ font: NSFont) -> Bool {
        font.isFixedPitch || font.fontDescriptor.symbolicTraits.contains(.monoSpace)
    }

    static func paragraphStyle(_ type: BlockType) -> NSParagraphStyle {
        let p = NSMutableParagraphStyle()
        p.paragraphSpacing = 4
        switch type {
        case .bulleted, .numbered:
            p.textLists = [NSTextList(markerFormat: type == .numbered ? .decimal : .disc, options: 0)]
            p.tabStops = [NSTextTab(textAlignment: .left, location: 8), NSTextTab(textAlignment: .left, location: listIndent)]
            p.firstLineHeadIndent = 0
            p.headIndent = listIndent
        case .quote, .code:
            p.firstLineHeadIndent = indent
            p.headIndent = indent
            if type == .code { p.paragraphSpacing = 0 }
        case .heading:
            p.paragraphSpacingBefore = 8
            p.paragraphSpacing = 6
        case .paragraph, .todo:
            break
        }
        return p
    }

    static func baseAttributes(_ type: BlockType, level: Int = 1) -> [NSAttributedString.Key: Any] {
        var a: [NSAttributedString.Key: Any] = [
            .paragraphStyle: paragraphStyle(type),
            .foregroundColor: NSColor.textColor,
        ]
        switch type {
        case .heading:
            a[.font] = headingFont(level)
        case .code:
            a[.font] = monoFont
            a[.backgroundColor] = NSColor.quaternaryLabelColor
        case .quote:
            a[.font] = bodyFont
            a[.foregroundColor] = NSColor.secondaryLabelColor
        case .paragraph, .bulleted, .numbered, .todo:
            a[.font] = bodyFont
        }
        return a
    }

    static func listMarker(_ type: BlockType, number: Int) -> String {
        type == .numbered ? "\t\(number).\t" : "\t•\t"
    }
}

/// PageDocument ⇄ NSAttributedString.
enum AttributedConverter {
    // MARK: document -> attributed string

    static func attributedString(from doc: PageDocument) -> NSAttributedString {
        let out = NSMutableAttributedString()
        var number = 0
        for (i, block) in doc.blocks.enumerated() {
            number = block.type == .numbered ? number + 1 : 0
            out.append(render(block, number: number))
            if i < doc.blocks.count - 1 {
                out.append(NSAttributedString(string: "\n", attributes: EditorStyle.baseAttributes(block.type, level: block.level)))
            }
        }
        return out
    }

    /// One block without a trailing paragraph separator.
    static func render(_ block: Block, number: Int) -> NSAttributedString {
        let base = EditorStyle.baseAttributes(block.type, level: block.level)
        let m = NSMutableAttributedString()
        switch block.type {
        case .bulleted, .numbered:
            m.append(NSAttributedString(string: EditorStyle.listMarker(block.type, number: number), attributes: base))
        case .todo:
            m.append(NSAttributedString(string: block.checked ? EditorStyle.checkedBox : EditorStyle.uncheckedBox, attributes: base))
        default:
            break
        }
        if block.type == .code {
            m.append(NSAttributedString(string: block.text, attributes: base))
            return m
        }
        for span in block.spans {
            m.append(NSAttributedString(string: span.text, attributes: spanAttributes(span, block: block, base: base)))
        }
        return m
    }

    static func spanAttributes(_ span: TextSpan, block: Block, base: [NSAttributedString.Key: Any]) -> [NSAttributedString.Key: Any] {
        var a = base
        let fm = NSFontManager.shared
        var font: NSFont = span.code ? EditorStyle.monoFont : ((base[.font] as? NSFont) ?? EditorStyle.bodyFont)
        if span.bold && block.type != .heading { font = fm.convert(font, toHaveTrait: .boldFontMask) }
        if span.italic { font = fm.convert(font, toHaveTrait: .italicFontMask) }
        a[.font] = font
        if span.code { a[.backgroundColor] = NSColor.quaternaryLabelColor }
        if let link = span.link {
            if let url = URL(string: link) { a[.link] = url } else { a[.link] = link }
        }
        return a
    }

    // MARK: attributed string -> document

    static func document(from attr: NSAttributedString) -> PageDocument {
        let ns = attr.string as NSString
        var blocks: [Block] = []
        var location = 0
        while location < ns.length {
            let paragraph = ns.paragraphRange(for: NSRange(location: location, length: 0))
            var contentEnd = NSMaxRange(paragraph)
            while contentEnd > paragraph.location, isParagraphTerminator(ns.character(at: contentEnd - 1)) { contentEnd -= 1 }
            let content = NSRange(location: paragraph.location, length: contentEnd - paragraph.location)
            blocks.append(readParagraph(attr, ns: ns, content: content, paragraph: paragraph))
            location = max(NSMaxRange(paragraph), location + 1)
        }
        // A trailing paragraph separator starts an (empty) last line; keep it as an empty block.
        if ns.length > 0, isParagraphTerminator(ns.character(at: ns.length - 1)) {
            blocks.append(Block(.paragraph))
        }

        // Code blocks span several paragraphs (one per line); merge consecutive code paragraphs.
        var merged: [Block] = []
        for block in blocks {
            if block.type == .code, let last = merged.last, last.type == .code {
                merged[merged.count - 1].spans = [TextSpan(last.text + "\n" + block.text)]
            } else {
                merged.append(block)
            }
        }
        return PageDocument(merged).normalized()
    }

    private static func isParagraphTerminator(_ c: unichar) -> Bool { c == 0x0A || c == 0x0D || c == 0x2029 }

    private static func readParagraph(_ attr: NSAttributedString, ns: NSString, content: NSRange, paragraph: NSRange) -> Block {
        // Attributes come from the first character, or from the paragraph terminator when the line is empty.
        let attrIndex: Int? = content.length > 0 ? content.location : (paragraph.length > 0 ? paragraph.location : nil)
        let attrs = attrIndex.map { attr.attributes(at: $0, effectiveRange: nil) } ?? [:]
        let style = attrs[.paragraphStyle] as? NSParagraphStyle
        let text = ns.substring(with: content)
        var body = content
        var block = Block(.paragraph)

        if let list = style?.textLists.last {
            block.type = isBulletFormat(list.markerFormat) ? .bulleted : .numbered
            // Strip the "\t<marker>\t" prefix.
            if text.hasPrefix("\t") {
                let rest = (text as NSString).substring(from: 1) as NSString
                let tab = rest.range(of: "\t")
                if tab.location != NSNotFound {
                    let prefixLength = 1 + tab.location + 1
                    body = NSRange(location: content.location + prefixLength, length: content.length - prefixLength)
                }
            }
        } else if text.hasPrefix("☐") || text.hasPrefix("☑") {
            block.type = .todo
            block.checked = text.hasPrefix("☑")
            let prefixLength = (text as NSString).length >= 2 && (text as NSString).character(at: 1) == 0x20 ? 2 : 1
            body = NSRange(location: content.location + prefixLength, length: content.length - prefixLength)
        } else if max(style?.firstLineHeadIndent ?? 0, style?.headIndent ?? 0) >= 18 {
            block.type = isAllMonospaced(attr, body, fallback: attrs[.font] as? NSFont) ? .code : .quote
        } else if let level = EditorStyle.headingLevel(forSize: maxFontSize(attr, body, fallback: attrs[.font] as? NSFont)) {
            block.type = .heading
            block.level = level
        }

        if block.type == .code {
            let raw = ns.substring(with: body)
                .replacingOccurrences(of: "\u{2028}", with: "\n")
                .replacingOccurrences(of: "\u{FFFC}", with: "")
            block.spans = [TextSpan(raw)]
            return block
        }

        var spans: [TextSpan] = []
        if body.length > 0 {
            attr.enumerateAttributes(in: body, options: []) { a, range, _ in
                let piece = ns.substring(with: range)
                    .replacingOccurrences(of: "\u{2028}", with: " ")
                    .replacingOccurrences(of: "\u{FFFC}", with: "")
                if piece.isEmpty { return }
                var span = TextSpan(piece)
                if let font = a[.font] as? NSFont {
                    let traits = font.fontDescriptor.symbolicTraits
                    span.bold = traits.contains(.bold) && block.type != .heading
                    span.italic = traits.contains(.italic)
                    span.code = EditorStyle.isMonospaced(font)
                    if span.code { span.bold = false }
                }
                if let url = a[.link] as? URL {
                    span.link = url.absoluteString
                } else if let s = a[.link] as? String {
                    span.link = s
                }
                spans.append(span)
            }
        }
        block.spans = spans
        return block
    }

    private static func isBulletFormat(_ format: NSTextList.MarkerFormat) -> Bool {
        [.disc, .circle, .square, .hyphen, .box, .check, .diamond].contains(format)
    }

    private static func isAllMonospaced(_ attr: NSAttributedString, _ range: NSRange, fallback: NSFont?) -> Bool {
        if range.length == 0 { return fallback.map(EditorStyle.isMonospaced) ?? false }
        var all = true
        attr.enumerateAttribute(.font, in: range, options: []) { value, _, stop in
            if let font = value as? NSFont, EditorStyle.isMonospaced(font) { return }
            all = false
            stop.pointee = true
        }
        return all
    }

    private static func maxFontSize(_ attr: NSAttributedString, _ range: NSRange, fallback: NSFont?) -> CGFloat {
        if range.length == 0 { return fallback?.pointSize ?? EditorStyle.bodySize }
        var size: CGFloat = 0
        attr.enumerateAttribute(.font, in: range, options: []) { value, _, _ in
            if let font = value as? NSFont, !EditorStyle.isMonospaced(font) { size = max(size, font.pointSize) }
        }
        return size == 0 ? (fallback?.pointSize ?? EditorStyle.bodySize) : size
    }
}
