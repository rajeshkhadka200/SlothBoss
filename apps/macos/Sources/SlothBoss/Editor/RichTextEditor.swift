import AppKit
import SwiftUI
import SlothBossCore

/// Lets SwiftUI toolbar buttons drive the AppKit text view.
@MainActor
final class EditorController {
    weak var textView: SBTextView?

    private func perform(_ action: (SBTextView) -> Void) {
        guard let tv = textView else { return }
        tv.window?.makeFirstResponder(tv)
        action(tv)
    }

    func toggleBold() { perform { $0.toggleTrait(.boldFontMask) } }
    func toggleItalic() { perform { $0.toggleTrait(.italicFontMask) } }
    func toggleInlineCode() { perform { $0.toggleInlineCode() } }
    func heading(_ level: Int) { perform { $0.toggleBlockType(.heading, level: level) } }
    func bodyText() { perform { $0.setBodyText() } }
    func toggle(_ type: BlockType) { perform { $0.toggleBlockType(type) } }
    func toggleChecked() { perform { $0.toggleTodoOrChecked() } }
    func editLink() { perform { $0.editLink() } }
    func linkToPage(id: String, title: String) { perform { $0.applyLink(Links.forPage(id), title: title) } }
    func undo() { perform { $0.undoManager?.undo() } }
    func redo() { perform { $0.undoManager?.redo() } }
}

/// NSTextView-based rich text editor bound to a PageDocument.
///
/// The text view owns the editing state; SwiftUI only supplies the initial document and a
/// `revision` that, when changed, replaces the content (used when the page changes elsewhere).
struct RichTextEditor: NSViewRepresentable {
    let document: PageDocument
    let revision: Int
    let controller: EditorController
    let onChange: (PageDocument) -> Void
    let onOpenPage: (String) -> Void

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeNSView(context: Context) -> NSScrollView {
        let textView = SBTextView.make()
        textView.delegate = context.coordinator
        textView.textStorage?.setAttributedString(AttributedConverter.attributedString(from: document))
        textView.typingAttributes = EditorStyle.baseAttributes(.paragraph)

        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.hasHorizontalScroller = false
        scroll.autohidesScrollers = true
        scroll.borderType = .noBorder
        scroll.drawsBackground = true
        scroll.backgroundColor = NSColor.textBackgroundColor
        scroll.documentView = textView

        controller.textView = textView
        context.coordinator.lastRevision = revision
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        context.coordinator.parent = self
        guard let textView = scroll.documentView as? SBTextView else { return }
        controller.textView = textView
        if context.coordinator.lastRevision != revision {
            context.coordinator.lastRevision = revision
            context.coordinator.isApplyingExternalChange = true
            textView.textStorage?.setAttributedString(AttributedConverter.attributedString(from: document))
            // The old undo history refers to text that no longer exists.
            textView.undoManager?.removeAllActions()
            context.coordinator.isApplyingExternalChange = false
        }
    }

    final class Coordinator: NSObject, NSTextViewDelegate {
        var parent: RichTextEditor
        var lastRevision = 0
        var isApplyingExternalChange = false

        init(_ parent: RichTextEditor) { self.parent = parent }

        func textDidChange(_ notification: Notification) {
            guard !isApplyingExternalChange, let textView = notification.object as? NSTextView, let storage = textView.textStorage else { return }
            parent.onChange(AttributedConverter.document(from: storage))
        }

        func textView(_ textView: NSTextView, clickedOnLink link: Any, at charIndex: Int) -> Bool {
            let target = (link as? URL)?.absoluteString ?? (link as? String) ?? ""
            if let pageId = Links.parsePageLink(target) {
                parent.onOpenPage(pageId)
                return true
            }
            // Web and mail links open in the user's default apps; anything else is ignored.
            if Links.isAllowed(target), let url = URL(string: target) {
                NSWorkspace.shared.open(url)
            }
            return true
        }
    }
}
