import SwiftUI
import SlothBossCore

struct HomeView: View {
    @EnvironmentObject private var app: AppModel
    @State private var recent: [PageSummary] = []
    @State private var dueSoon: [TaskItem] = []
    @State private var overdue: [TaskItem] = []
    @State private var error: String?

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 24) {
                VStack(alignment: .leading, spacing: 6) {
                    Text("SlothBoss").font(.largeTitle.bold())
                    Text("Your private workspace. Everything stays on this Mac.")
                        .foregroundStyle(.secondary)
                }
                HStack(spacing: 12) {
                    Button { app.newPage(parentId: nil) } label: { Label("New Page", systemImage: "doc.badge.plus") }
                    Button { app.newTask() } label: { Label("New Task", systemImage: "plus.circle") }
                    Button { app.focusSearch() } label: { Label("Search", systemImage: "magnifyingglass") }
                    Button { app.showAssistantInspector = true } label: { Label("Assistant", systemImage: "sparkles") }
                }
                .controlSize(.large)

                if let error {
                    Label(error, systemImage: "exclamationmark.triangle").foregroundStyle(.red)
                }

                section("Recent pages") {
                    if recent.isEmpty {
                        Text("No pages yet. Create one with ⌘N.").foregroundStyle(.secondary)
                    }
                    ForEach(recent) { page in
                        Button { app.open(pageId: page.id) } label: {
                            HStack {
                                Label(page.displayTitle, systemImage: "doc.text")
                                Spacer()
                                Text(Self.shortDate(page.updatedAt)).font(.caption).foregroundStyle(.secondary)
                            }
                        }
                        .buttonStyle(.plain)
                    }
                }

                section("Overdue") {
                    if overdue.isEmpty { Text("Nothing overdue.").foregroundStyle(.secondary) }
                    ForEach(overdue) { taskRow($0) }
                }

                section("Due in the next 7 days") {
                    if dueSoon.isEmpty { Text("No upcoming due dates.").foregroundStyle(.secondary) }
                    ForEach(dueSoon) { taskRow($0) }
                }

                section("Local AI") {
                    ModelStatusRow(assistant: app.assistant)
                }
            }
            .padding(32)
            .frame(maxWidth: 720, alignment: .leading)
            .frame(maxWidth: .infinity, alignment: .leading)
        }
        .navigationTitle("Home")
        .onAppear(perform: reload)
        .onChange(of: app.revision) { reload() }
    }

    private func section<Content: View>(_ title: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: 8) {
            Text(title).font(.headline)
            content()
        }
    }

    private func taskRow(_ task: TaskItem) -> some View {
        Button { app.editTask(task) } label: {
            HStack {
                Image(systemName: task.isDone ? "checkmark.circle.fill" : "circle")
                Text(task.title)
                Spacer()
                if let due = task.dueDate { Text(due).font(.caption).foregroundStyle(.secondary) }
            }
        }
        .buttonStyle(.plain)
    }

    private func reload() {
        guard let ws = app.workspace else { return }
        do {
            recent = try ws.pages.recent(limit: 8)
            overdue = try ws.tasks.list(TaskFilter(due: .overdue))
            dueSoon = try ws.tasks.list(TaskFilter(due: .next7Days, hideDone: true))
            error = nil
        } catch {
            self.error = describeError(error)
        }
    }

    static func shortDate(_ stamp: String) -> String {
        String(stamp.prefix(10))
    }
}

struct ArchiveView: View {
    @EnvironmentObject private var app: AppModel

    var body: some View {
        Group {
            if app.archived.isEmpty {
                ContentUnavailableView("Archive is empty", systemImage: "archivebox",
                                       description: Text("Archived pages and their subpages appear here. Archive a page from its context menu."))
            } else {
                List {
                    ForEach(app.archived) { page in
                        HStack {
                            Label(page.displayTitle, systemImage: "doc.text")
                            Spacer()
                            Text("Archived \(HomeView.shortDate(page.updatedAt))").font(.caption).foregroundStyle(.secondary)
                            Button("Restore") { app.restore(page.id) }
                            Button("Delete…", role: .destructive) { app.requestDelete(page) }
                        }
                    }
                }
            }
        }
        .navigationTitle("Archive")
    }
}
