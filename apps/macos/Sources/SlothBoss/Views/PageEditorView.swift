import SwiftUI
import SlothBossCore

struct PageEditorView: View {
    @EnvironmentObject private var app: AppModel
    @StateObject private var model: PageEditorModel
    @State private var showInfo = true
    @FocusState private var titleFocused: Bool

    init(pageId: String, app: AppModel) {
        _model = StateObject(wrappedValue: PageEditorModel(pageId: pageId, app: app))
    }

    var body: some View {
        Group {
            if let error = model.loadError {
                ContentUnavailableView("Page unavailable", systemImage: "doc.questionmark", description: Text(error))
            } else {
                HStack(spacing: 0) {
                    editorColumn
                    if showInfo {
                        Divider()
                        PageSidePanel(model: model)
                            .frame(width: 260)
                    }
                }
            }
        }
        .navigationTitle(model.title.isEmpty ? "Untitled" : model.title)
        .toolbar {
            ToolbarItemGroup(placement: .primaryAction) {
                Button {
                    model.toggleFavorite()
                } label: {
                    Label(model.isFavorite ? "Remove from Favorites" : "Add to Favorites", systemImage: model.isFavorite ? "star.fill" : "star")
                }
                .help(model.isFavorite ? "Remove from Favorites" : "Add to Favorites")
                Button {
                    showInfo.toggle()
                } label: {
                    Label("Page Info", systemImage: "sidebar.right")
                }
                .help("Show or hide tasks, links and subpages")
            }
        }
        .onAppear {
            app.activeEditor = model
            if model.title.isEmpty { titleFocused = true }
        }
        .onDisappear {
            model.flush()
            if app.activeEditor === model { app.activeEditor = nil }
        }
    }

    private var editorColumn: some View {
        VStack(alignment: .leading, spacing: 0) {
            HStack(spacing: 8) {
                BreadcrumbBar(pages: model.breadcrumb) { app.open(pageId: $0) }
                Spacer()
                SaveStatusLabel(state: model.saveState)
            }
            .padding(.horizontal, 28)
            .padding(.top, 12)

            TextField("Untitled", text: $model.title)
                .textFieldStyle(.plain)
                .font(.system(size: 30, weight: .bold))
                .focused($titleFocused)
                .padding(.horizontal, 28)
                .padding(.top, 8)
                .onChange(of: model.title) { model.titleChanged() }
                .onSubmit {
                    if let textView = model.controller.textView { textView.window?.makeFirstResponder(textView) }
                }

            FormattingBar(controller: model.controller, pages: app.allPages.filter { $0.id != model.pageId })
                .padding(.horizontal, 22)
                .padding(.vertical, 6)

            Divider()

            RichTextEditor(
                document: model.document,
                revision: model.revision,
                controller: model.controller,
                onChange: { model.documentChanged($0) },
                onOpenPage: { app.open(pageId: $0) })
        }
    }
}

struct SaveStatusLabel: View {
    let state: PageEditorModel.SaveState

    var body: some View {
        switch state {
        case .saved:
            Label("Saved", systemImage: "checkmark.circle")
                .labelStyle(.titleAndIcon)
                .font(.caption)
                .foregroundStyle(.secondary)
        case .pending, .saving:
            Label("Saving…", systemImage: "arrow.triangle.2.circlepath")
                .labelStyle(.titleAndIcon)
                .font(.caption)
                .foregroundStyle(.secondary)
        case .failed(let message):
            Label("Not saved", systemImage: "exclamationmark.triangle.fill")
                .labelStyle(.titleAndIcon)
                .font(.caption)
                .foregroundStyle(.red)
                .help(message)
        }
    }
}

struct BreadcrumbBar: View {
    let pages: [PageSummary]
    let open: (String) -> Void

    var body: some View {
        HStack(spacing: 4) {
            ForEach(Array(pages.enumerated()), id: \.element.id) { index, page in
                if index > 0 {
                    Image(systemName: "chevron.right").font(.caption2).foregroundStyle(.tertiary)
                }
                if index == pages.count - 1 {
                    Text(page.displayTitle).font(.caption).foregroundStyle(.secondary).lineLimit(1)
                } else {
                    Button(page.displayTitle) { open(page.id) }
                        .buttonStyle(.link)
                        .font(.caption)
                        .lineLimit(1)
                }
            }
        }
    }
}

struct FormattingBar: View {
    let controller: EditorController
    let pages: [PageSummary]

    var body: some View {
        HStack(spacing: 2) {
            Menu {
                Button("Body Text") { controller.bodyText() }
                Button("Heading 1  ⌘1") { controller.heading(1) }
                Button("Heading 2  ⌘2") { controller.heading(2) }
                Button("Heading 3  ⌘3") { controller.heading(3) }
            } label: {
                Label("Style", systemImage: "textformat.size")
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .help("Paragraph style")

            Divider().frame(height: 16).padding(.horizontal, 4)
            barButton("Bold", "bold", "Bold (⌘B)") { controller.toggleBold() }
            barButton("Italic", "italic", "Italic (⌘I)") { controller.toggleItalic() }
            barButton("Code", "chevron.left.forwardslash.chevron.right", "Inline code (⌘E)") { controller.toggleInlineCode() }
            barButton("Link", "link", "Link (⌘K)") { controller.editLink() }
            Menu {
                if pages.isEmpty {
                    Text("No other pages")
                } else {
                    ForEach(pages) { page in
                        Button(page.displayTitle) { controller.linkToPage(id: page.id, title: page.displayTitle) }
                    }
                }
            } label: {
                Label("Link to Page", systemImage: "link.badge.plus")
            }
            .menuStyle(.borderlessButton)
            .fixedSize()
            .help("Link the selection to another page")

            Divider().frame(height: 16).padding(.horizontal, 4)
            barButton("Bulleted List", "list.bullet", "Bulleted list (⇧⌘8)") { controller.toggle(.bulleted) }
            barButton("Numbered List", "list.number", "Numbered list (⇧⌘7)") { controller.toggle(.numbered) }
            barButton("Checklist", "checklist", "Checklist (⌘↩ checks items)") { controller.toggle(.todo) }
            barButton("Quote", "text.quote", "Quote") { controller.toggle(.quote) }
            barButton("Code Block", "curlybraces", "Code block") { controller.toggle(.code) }
            Spacer()
        }
        .labelStyle(.iconOnly)
    }

    private func barButton(_ title: String, _ symbol: String, _ help: String, action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Label(title, systemImage: symbol)
                .frame(width: 22, height: 20)
        }
        .buttonStyle(.borderless)
        .help(help)
    }
}

struct PageSidePanel: View {
    @EnvironmentObject private var app: AppModel
    @ObservedObject var model: PageEditorModel

    var body: some View {
        List {
            Section {
                if model.tasks.isEmpty {
                    Text("No linked tasks").foregroundStyle(.secondary)
                }
                ForEach(model.tasks) { task in
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Button {
                            model.setTaskDone(task, !task.isDone)
                        } label: {
                            Image(systemName: task.isDone ? "checkmark.circle.fill" : "circle")
                        }
                        .buttonStyle(.borderless)
                        .help(task.isDone ? "Reopen" : "Complete")
                        Button {
                            app.editTask(task)
                        } label: {
                            VStack(alignment: .leading, spacing: 1) {
                                Text(task.title).strikethrough(task.isDone).lineLimit(2)
                                if let due = task.dueDate {
                                    Text("Due \(due)").font(.caption).foregroundStyle(.secondary)
                                }
                            }
                        }
                        .buttonStyle(.plain)
                    }
                }
                Button {
                    app.newTask(pageId: model.pageId)
                } label: {
                    Label("Add Task", systemImage: "plus")
                }
                .buttonStyle(.borderless)
            } header: {
                Text("Tasks")
            }

            Section("Subpages") {
                if model.children.isEmpty { Text("No subpages").foregroundStyle(.secondary) }
                ForEach(model.children) { page in pageLink(page) }
                Button {
                    app.newPage(parentId: model.pageId)
                } label: {
                    Label("New Subpage", systemImage: "plus")
                }
                .buttonStyle(.borderless)
            }

            Section("Linked from") {
                if model.backlinks.isEmpty { Text("No backlinks").foregroundStyle(.secondary) }
                ForEach(model.backlinks) { page in pageLink(page) }
            }

            Section("Links to") {
                if model.outgoing.isEmpty { Text("No outgoing links").foregroundStyle(.secondary) }
                ForEach(model.outgoing) { page in pageLink(page) }
            }
        }
        .listStyle(.sidebar)
    }

    private func pageLink(_ page: PageSummary) -> some View {
        Button {
            app.open(pageId: page.id)
        } label: {
            Label(page.displayTitle, systemImage: page.isArchived ? "archivebox" : "doc.text")
        }
        .buttonStyle(.plain)
    }
}
