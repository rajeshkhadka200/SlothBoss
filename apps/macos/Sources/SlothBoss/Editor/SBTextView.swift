import AppKit
import SlothBossCore

/// The page editor's text view (TextKit 1, so list markers live in the text as AppKit expects).
/// Block formatting is implemented by converting the affected paragraphs to `Block`s, changing
/// them, and rendering them back, so the editor and the converter can never disagree.
final class SBTextView: NSTextView {
    static func make() -> SBTextView {
        let storage = NSTextStorage()
        let layout = NSLayoutManager()
        storage.addLayoutManager(layout)
        let container = NSTextContainer(size: NSSize(width: 0, height: CGFloat.greatestFiniteMagnitude))
        container.widthTracksTextView = true
        layout.addTextContainer(container)

        let tv = SBTextView(frame: NSRect(x: 0, y: 0, width: 600, height: 400), textContainer: container)
        tv.minSize = NSSize(width: 0, height: 0)
        tv.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        tv.isVerticallyResizable = true
        tv.isHorizontallyResizable = false
        tv.autoresizingMask = [.width]
        tv.isRichText = true
        tv.importsGraphics = false
        tv.allowsUndo = true
        tv.usesFontPanel = false
        tv.usesFindBar = true
        tv.isIncrementalSearchingEnabled = true
        tv.isAutomaticQuoteSubstitutionEnabled = false
        tv.isAutomaticDashSubstitutionEnabled = false
        tv.isAutomaticLinkDetectionEnabled = false
        tv.isContinuousSpellCheckingEnabled = true
        tv.textContainerInset = NSSize(width: 28, height: 18)
        tv.backgroundColor = NSColor.textBackgroundColor
        tv.drawsBackground = true
        tv.typingAttributes = EditorStyle.baseAttributes(.paragraph)
        return tv
    }

    private var storage: NSTextStorage? { textStorage }

    // MARK: plain-text paste

    override func paste(_ sender: Any?) { pasteAsPlainText(sender) }

    override var readablePasteboardTypes: [NSPasteboard.PasteboardType] { [.string] }

    // MARK: keyboard shortcuts

    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        guard window?.firstResponder === self else { return super.performKeyEquivalent(with: event) }
        let flags = event.modifierFlags.intersection(.deviceIndependentFlagsMask).subtracting([.capsLock, .numericPad, .function])
        let key = event.charactersIgnoringModifiers?.lowercased() ?? ""
        if flags == .command {
            switch key {
            case "b": toggleTrait(.boldFontMask); return true
            case "i": toggleTrait(.italicFontMask); return true
            case "e": toggleInlineCode(); return true
            case "k": editLink(); return true
            case "1": toggleBlockType(.heading, level: 1); return true
            case "2": toggleBlockType(.heading, level: 2); return true
            case "3": toggleBlockType(.heading, level: 3); return true
            default: break
            }
            if event.keyCode == 36 || event.keyCode == 76 { // Return / keypad Enter
                toggleTodoOrChecked()
                return true
            }
        }
        if flags == [.command, .shift] {
            switch event.keyCode {
            case 26: toggleBlockType(.numbered); return true // 7
            case 28: toggleBlockType(.bulleted); return true // 8
            default: break
            }
        }
        return super.performKeyEquivalent(with: event)
    }

    // MARK: newline behavior (headings end, lists and checklists continue)

    override func insertNewline(_ sender: Any?) {
        if !handleNewline() { super.insertNewline(sender) }
    }

    private func isTerminator(_ c: unichar) -> Bool { c == 0x0A || c == 0x0D || c == 0x2029 }

    /// The block at the insertion point (uses typing attributes for an empty last line).
    private func currentBlock() -> (block: Block, paragraph: NSRange)? {
        guard let storage else { return nil }
        let ns = string as NSString
        let location = selectedRange().location
        let paragraph = ns.paragraphRange(for: NSRange(location: min(location, ns.length), length: 0))
        let probe: NSAttributedString
        if paragraph.length == 0 {
            probe = NSAttributedString(string: "\n", attributes: typingAttributes)
        } else {
            probe = storage.attributedSubstring(from: paragraph)
        }
        let block = AttributedConverter.document(from: probe).blocks.first ?? Block(.paragraph)
        return (block, paragraph)
    }

    private func handleNewline() -> Bool {
        guard selectedRange().length == 0, let current = currentBlock() else { return false }
        let block = current.block
        let isEmpty = block.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
        switch block.type {
        case .heading:
            insertText("\n", replacementRange: selectedRange())
            typingAttributes = EditorStyle.baseAttributes(.paragraph)
            return true
        case .todo:
            if isEmpty {
                toggleBlockType(.todo) // empty item: leave the checklist
                return true
            }
            insertText("\n", replacementRange: selectedRange())
            typingAttributes = EditorStyle.baseAttributes(.todo)
            insertText(EditorStyle.uncheckedBox, replacementRange: selectedRange())
            return true
        case .bulleted, .numbered:
            if isEmpty {
                toggleBlockType(block.type) // empty item: leave the list
                return true
            }
            insertText("\n", replacementRange: selectedRange())
            typingAttributes = EditorStyle.baseAttributes(block.type)
            insertText(EditorStyle.listMarker(block.type, number: 1), replacementRange: selectedRange())
            if block.type == .numbered { renumberLists() }
            return true
        default:
            return false
        }
    }

    // MARK: block formatting

    /// Replaces the paragraphs touched by the selection with transformed blocks (undoable).
    func transformSelectedBlocks(_ transform: (inout [Block]) -> Void) {
        guard let storage else { return }
        let ns = string as NSString
        let sel = selectedRange()
        let range = ns.paragraphRange(for: NSRange(location: min(sel.location, ns.length), length: min(sel.length, ns.length - min(sel.location, ns.length))))
        let hasTerminator = range.length > 0 && isTerminator(ns.character(at: NSMaxRange(range) - 1))

        // Read the paragraphs (including the terminator so empty lines keep their style).
        var blocks = AttributedConverter.document(from: storage.attributedSubstring(from: range)).blocks
        // The reader turns a trailing separator into an extra empty line; the separator is re-added below.
        if hasTerminator, blocks.count > 1 { blocks.removeLast() }
        if blocks.isEmpty {
            let probe = NSAttributedString(string: "\n", attributes: typingAttributes)
            blocks = [AttributedConverter.document(from: probe).blocks.first ?? Block(.paragraph)]
        }
        transform(&blocks)

        let replacement = NSMutableAttributedString(attributedString: AttributedConverter.attributedString(from: PageDocument(blocks)))
        let last = blocks.last ?? Block(.paragraph)
        if hasTerminator {
            replacement.append(NSAttributedString(string: "\n", attributes: EditorStyle.baseAttributes(last.type, level: last.level)))
        }
        guard shouldChangeText(in: range, replacementString: replacement.string) else { return }
        storage.replaceCharacters(in: range, with: replacement)
        didChangeText()
        let caret = range.location + replacement.length - (hasTerminator ? 1 : 0)
        setSelectedRange(NSRange(location: caret, length: 0))
        typingAttributes = EditorStyle.baseAttributes(last.type, level: last.level)
        renumberLists()
    }

    private static func convertBlock(_ block: inout Block, to type: BlockType, level: Int) {
        if type == .code {
            block.spans = [TextSpan(block.text)]
        } else if block.type == .code {
            block.spans = [TextSpan(block.text.newlinesToSpacesForEditor)]
        }
        block.type = type
        block.level = type == .heading ? level : 0
        if type != .todo { block.checked = false }
    }

    /// Applies `type` to the selected paragraphs, or turns them back into body text if they all have it.
    func toggleBlockType(_ type: BlockType, level: Int = 1) {
        transformSelectedBlocks { blocks in
            let allSame = blocks.allSatisfy { $0.type == type && (type != .heading || $0.level == level) }
            for i in blocks.indices {
                SBTextView.convertBlock(&blocks[i], to: allSame ? .paragraph : type, level: level)
            }
        }
    }

    func setBodyText() {
        transformSelectedBlocks { blocks in
            for i in blocks.indices { SBTextView.convertBlock(&blocks[i], to: .paragraph, level: 0) }
        }
    }

    /// ⌘↩: checks/unchecks checklist items, or turns other paragraphs into checklist items.
    func toggleTodoOrChecked() {
        transformSelectedBlocks { blocks in
            let allTodo = blocks.allSatisfy { $0.type == .todo }
            for i in blocks.indices {
                if allTodo { blocks[i].checked.toggle() } else { SBTextView.convertBlock(&blocks[i], to: .todo, level: 0) }
            }
        }
    }

    /// Rewrites "\tN.\t" markers so consecutive numbered paragraphs count 1, 2, 3…
    func renumberLists() {
        guard let storage else { return }
        let ns = string as NSString
        var edits: [(range: NSRange, text: String)] = []
        var location = 0
        var number = 0
        while location < ns.length {
            let paragraph = ns.paragraphRange(for: NSRange(location: location, length: 0))
            let style = storage.attribute(.paragraphStyle, at: paragraph.location, effectiveRange: nil) as? NSParagraphStyle
            if let list = style?.textLists.last, list.markerFormat == .decimal {
                number += 1
                let text = ns.substring(with: paragraph) as NSString
                if text.length > 1, text.character(at: 0) == 0x09 {
                    let second = text.range(of: "\t", options: [], range: NSRange(location: 1, length: text.length - 1))
                    if second.location != NSNotFound {
                        let markerRange = NSRange(location: paragraph.location, length: second.location + 1)
                        let expected = EditorStyle.listMarker(.numbered, number: number)
                        if ns.substring(with: markerRange) != expected { edits.append((markerRange, expected)) }
                    }
                }
            } else {
                number = 0
            }
            location = max(NSMaxRange(paragraph), location + 1)
        }
        guard !edits.isEmpty else { return }
        var selection = selectedRange()
        for edit in edits.reversed() {
            guard shouldChangeText(in: edit.range, replacementString: edit.text) else { continue }
            storage.replaceCharacters(in: edit.range, with: edit.text)
            didChangeText()
            if edit.range.location < selection.location {
                selection.location += (edit.text as NSString).length - edit.range.length
            }
        }
        setSelectedRange(NSRange(location: min(selection.location, (string as NSString).length), length: 0))
    }

    // MARK: inline formatting

    func toggleTrait(_ trait: NSFontTraitMask) {
        let fm = NSFontManager.shared
        let sel = selectedRange()
        if sel.length == 0 {
            var attrs = typingAttributes
            let font = (attrs[.font] as? NSFont) ?? EditorStyle.bodyFont
            attrs[.font] = fm.traits(of: font).contains(trait) ? fm.convert(font, toNotHaveTrait: trait) : fm.convert(font, toHaveTrait: trait)
            typingAttributes = attrs
            return
        }
        guard let storage else { return }
        var allHave = true
        storage.enumerateAttribute(.font, in: sel, options: []) { value, _, stop in
            let font = (value as? NSFont) ?? EditorStyle.bodyFont
            if !fm.traits(of: font).contains(trait) { allHave = false; stop.pointee = true }
        }
        guard shouldChangeText(in: sel, replacementString: nil) else { return }
        storage.beginEditing()
        storage.enumerateAttribute(.font, in: sel, options: []) { value, range, _ in
            let font = (value as? NSFont) ?? EditorStyle.bodyFont
            let converted = allHave ? fm.convert(font, toNotHaveTrait: trait) : fm.convert(font, toHaveTrait: trait)
            storage.addAttribute(.font, value: converted, range: range)
        }
        storage.endEditing()
        didChangeText()
    }

    func toggleInlineCode() {
        let sel = selectedRange()
        if sel.length == 0 {
            var attrs = typingAttributes
            let font = (attrs[.font] as? NSFont) ?? EditorStyle.bodyFont
            if EditorStyle.isMonospaced(font) {
                attrs[.font] = EditorStyle.bodyFont
                attrs[.backgroundColor] = nil
            } else {
                attrs[.font] = EditorStyle.monoFont
                attrs[.backgroundColor] = NSColor.quaternaryLabelColor
            }
            typingAttributes = attrs
            return
        }
        guard let storage else { return }
        var allMono = true
        storage.enumerateAttribute(.font, in: sel, options: []) { value, _, stop in
            if let font = value as? NSFont, EditorStyle.isMonospaced(font) { return }
            allMono = false
            stop.pointee = true
        }
        guard shouldChangeText(in: sel, replacementString: nil) else { return }
        storage.beginEditing()
        if allMono {
            storage.addAttribute(.font, value: EditorStyle.bodyFont, range: sel)
            storage.removeAttribute(.backgroundColor, range: sel)
        } else {
            storage.addAttribute(.font, value: EditorStyle.monoFont, range: sel)
            storage.addAttribute(.backgroundColor, value: NSColor.quaternaryLabelColor, range: sel)
        }
        storage.endEditing()
        didChangeText()
    }

    // MARK: links

    private func linkValue(at index: Int, effectiveRange: UnsafeMutablePointer<NSRange>? = nil) -> String? {
        guard let storage, index >= 0, index < storage.length else { return nil }
        let value = storage.attribute(.link, at: index, effectiveRange: effectiveRange)
        if let url = value as? URL { return url.absoluteString }
        return value as? String
    }

    /// ⌘K: prompts for a link URL (web, mail or slothboss:// page link) for the selection.
    func editLink() {
        let sel = selectedRange()
        let existing = linkValue(at: sel.location)
        let alert = NSAlert()
        alert.messageText = existing == nil ? "Add Link" : "Edit Link"
        alert.informativeText = "Enter a web address (https://…), an email link (mailto:…), or a page link (slothboss://page/…)."
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 340, height: 24))
        field.stringValue = existing ?? ""
        field.placeholderString = "https://"
        alert.accessoryView = field
        alert.addButton(withTitle: "Apply")
        alert.addButton(withTitle: "Cancel")
        if existing != nil { alert.addButton(withTitle: "Remove Link") }
        alert.window.initialFirstResponder = field
        let response = alert.runModal()
        if response == .alertFirstButtonReturn {
            let url = field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
            guard Links.isAllowed(url) else {
                let warning = NSAlert()
                warning.messageText = "That link can't be used"
                warning.informativeText = "Only http(s), mailto and slothboss://page/ links are allowed."
                warning.runModal()
                return
            }
            applyLink(url, title: nil)
        } else if response == .alertThirdButtonReturn {
            applyLink(nil, title: nil)
        }
        window?.makeFirstResponder(self)
    }

    /// Sets (or with nil removes) a link on the selection. With an empty selection, inserts `title`
    /// (or the URL) as linked text.
    func applyLink(_ url: String?, title: String?) {
        guard let storage else { return }
        var sel = selectedRange()
        if sel.length == 0 {
            if let url {
                let text = title ?? url
                var attrs = typingAttributes
                if let u = URL(string: url) { attrs[.link] = u } else { attrs[.link] = url }
                guard shouldChangeText(in: sel, replacementString: text) else { return }
                storage.replaceCharacters(in: sel, with: NSAttributedString(string: text, attributes: attrs))
                didChangeText()
                setSelectedRange(NSRange(location: sel.location + (text as NSString).length, length: 0))
                var plain = typingAttributes
                plain[.link] = nil
                typingAttributes = plain
                return
            }
            var effective = NSRange(location: 0, length: 0)
            guard linkValue(at: sel.location, effectiveRange: &effective) != nil else { return }
            sel = effective
        }
        guard shouldChangeText(in: sel, replacementString: nil) else { return }
        if let url {
            if let u = URL(string: url) {
                storage.addAttribute(.link, value: u, range: sel)
            } else {
                storage.addAttribute(.link, value: url, range: sel)
            }
        } else {
            storage.removeAttribute(.link, range: sel)
        }
        didChangeText()
    }

    // MARK: clickable checkboxes

    override func mouseDown(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        if toggleCheckbox(at: point) { return }
        super.mouseDown(with: event)
    }

    private func toggleCheckbox(at point: NSPoint) -> Bool {
        guard let storage, let layoutManager, let textContainer else { return false }
        let ns = string as NSString
        guard ns.length > 0 else { return false }
        let index = characterIndexForInsertion(at: point)
        for candidate in [index, index - 1] where candidate >= 0 && candidate < ns.length {
            let c = ns.character(at: candidate)
            guard c == 0x2610 || c == 0x2611 else { continue }
            let paragraph = ns.paragraphRange(for: NSRange(location: candidate, length: 0))
            guard paragraph.location == candidate else { continue }
            let glyphs = layoutManager.glyphRange(forCharacterRange: NSRange(location: candidate, length: 1), actualCharacterRange: nil)
            var rect = layoutManager.boundingRect(forGlyphRange: glyphs, in: textContainer)
            rect.origin.x += textContainerOrigin.x
            rect.origin.y += textContainerOrigin.y
            guard rect.insetBy(dx: -3, dy: -3).contains(point) else { continue }
            let replacement = c == 0x2610 ? "☑" : "☐"
            let range = NSRange(location: candidate, length: 1)
            guard shouldChangeText(in: range, replacementString: replacement) else { return true }
            storage.replaceCharacters(in: range, with: replacement)
            didChangeText()
            return true
        }
        return false
    }
}

extension String {
    /// Newlines become spaces (code block text turned into a regular paragraph).
    var newlinesToSpacesForEditor: String {
        replacingOccurrences(of: "\r\n", with: " ").replacingOccurrences(of: "\n", with: " ").replacingOccurrences(of: "\r", with: " ")
    }
}
