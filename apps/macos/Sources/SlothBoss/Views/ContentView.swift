import AppKit
import SwiftUI
import SlothBossCore

struct ContentView: View {
    @EnvironmentObject private var app: AppModel

    var body: some View {
        if let error = app.openError {
            WorkspaceErrorView(message: error)
        } else if app.workspace == nil {
            ProgressView("Opening workspace…")
                .frame(maxWidth: .infinity, maxHeight: .infinity)
        } else {
            mainView
        }
    }

    private var mainView: some View {
        NavigationSplitView {
            SidebarView()
                .navigationSplitViewColumnWidth(min: 200, ideal: 240, max: 360)
        } detail: {
            DetailView()
                .inspector(isPresented: $app.showAssistantInspector) {
                    AssistantView(assistant: app.assistant, compact: true)
                        .inspectorColumnWidth(min: 300, ideal: 360, max: 520)
                }
                .toolbar {
                    ToolbarItem(placement: .primaryAction) {
                        Button {
                            app.toggleAssistant()
                        } label: {
                            Label("Assistant", systemImage: "sparkles")
                        }
                        .help("Show or hide the assistant (⌘J)")
                    }
                }
        }
        .sheet(item: $app.renameTarget) { page in
            RenameSheet(page: page)
        }
        .sheet(item: $app.taskEditor) { request in
            TaskEditorSheet(request: request)
        }
        .confirmationDialog(
            "Delete “\(app.deleteRequest?.page.displayTitle ?? "")”?",
            isPresented: Binding(get: { app.deleteRequest != nil }, set: { if !$0 { app.deleteRequest = nil } }),
            titleVisibility: .visible,
            presenting: app.deleteRequest
        ) { request in
            Button("Delete", role: .destructive) { app.confirmDelete(request) }
            Button("Cancel", role: .cancel) {}
        } message: { request in
            Text(request.message)
        }
        .alert(
            app.alert?.title ?? "",
            isPresented: Binding(get: { app.alert != nil }, set: { if !$0 { app.alert = nil } }),
            presenting: app.alert
        ) { _ in
            Button("OK", role: .cancel) {}
        } message: { alert in
            Text(alert.message)
        }
    }
}

struct DetailView: View {
    @EnvironmentObject private var app: AppModel

    var body: some View {
        switch app.selection {
        case .page(let id):
            PageEditorView(pageId: id, app: app).id(id)
        case .search:
            SearchView()
        case .tasks:
            TasksView()
        case .assistant:
            AssistantView(assistant: app.assistant, compact: false)
                .navigationTitle("Assistant")
        case .archive:
            ArchiveView()
        case .home, .none:
            HomeView()
        }
    }
}

struct WorkspaceErrorView: View {
    let message: String

    var body: some View {
        ContentUnavailableView {
            Label("The workspace could not be opened", systemImage: "exclamationmark.triangle")
        } description: {
            Text(message)
        } actions: {
            Button("Quit SlothBoss") { NSApplication.shared.terminate(nil) }
        }
        .frame(minWidth: 480, minHeight: 320)
    }
}

struct SidebarView: View {
    @EnvironmentObject private var app: AppModel

    var body: some View {
        List(selection: $app.selection) {
            Section {
                Label("Home", systemImage: "house").tag(SidebarItem.home)
                Label("Search", systemImage: "magnifyingglass").tag(SidebarItem.search)
                Label("Tasks", systemImage: "checklist").tag(SidebarItem.tasks)
                Label("Assistant", systemImage: "sparkles").tag(SidebarItem.assistant)
                Label("Archive", systemImage: "archivebox").tag(SidebarItem.archive)
            }

            if !app.favorites.isEmpty {
                Section("Favorites") {
                    ForEach(app.favorites) { page in
                        PageRow(page: page, icon: "star").tag(SidebarItem.page(page.id))
                    }
                }
            }

            Section {
                if app.tree.isEmpty {
                    Text("No pages yet").foregroundStyle(.secondary)
                }
                OutlineGroup(app.tree, children: \.children) { node in
                    PageRow(page: node.page, icon: "doc.text").tag(SidebarItem.page(node.id))
                }
            } header: {
                HStack {
                    Text("Pages")
                    Spacer()
                    Button {
                        app.newPage(parentId: nil)
                    } label: {
                        Image(systemName: "plus")
                    }
                    .buttonStyle(.borderless)
                    .help("New Page (⌘N)")
                }
            }
        }
        .listStyle(.sidebar)
    }
}

struct PageRow: View {
    @EnvironmentObject private var app: AppModel
    let page: PageSummary
    let icon: String

    var body: some View {
        Label(page.displayTitle, systemImage: icon)
            .lineLimit(1)
            .contextMenu {
                Button("New Subpage") { app.newPage(parentId: page.id) }
                Button("Rename…") { app.renameTarget = page }
                Button(page.isFavorite ? "Remove from Favorites" : "Add to Favorites") { app.toggleFavorite(page) }
                Divider()
                Button("Export as Markdown…") { app.exportMarkdown(pageId: page.id) }
                Divider()
                Button("Archive") { app.archive(page.id) }
                Button("Delete…", role: .destructive) { app.requestDelete(page) }
            }
    }
}

struct RenameSheet: View {
    @EnvironmentObject private var app: AppModel
    @Environment(\.dismiss) private var dismiss
    let page: PageSummary
    @State private var title = ""

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Rename Page").font(.headline)
            TextField("Untitled", text: $title)
                .textFieldStyle(.roundedBorder)
                .frame(width: 320)
                .onSubmit(save)
            HStack {
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button("Rename", action: save)
                    .keyboardShortcut(.defaultAction)
            }
        }
        .padding(20)
        .onAppear { title = page.title }
    }

    private func save() {
        app.rename(page, to: title)
        dismiss()
    }
}
