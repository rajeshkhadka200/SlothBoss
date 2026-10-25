import SwiftUI
import SlothBossCore

struct TasksView: View {
    @EnvironmentObject private var app: AppModel
    @State private var status: String?
    @State private var priority: String?
    @State private var due: DueFilter = .any
    @State private var hideDone = false
    @State private var tasks: [TaskItem] = []
    @State private var selection = Set<String>()
    @State private var pendingDelete: TaskItem?
    @State private var error: String?

    private var filter: TaskFilter { TaskFilter(status: status, priority: priority, due: due, hideDone: hideDone) }

    var body: some View {
        VStack(spacing: 0) {
            filterBar
                .padding(.horizontal, 16)
                .padding(.vertical, 8)
            Divider()
            if let error {
                ContentUnavailableView("Tasks could not be loaded", systemImage: "exclamationmark.triangle", description: Text(error))
            } else if tasks.isEmpty {
                ContentUnavailableView {
                    Label(filter == TaskFilter() ? "No tasks yet" : "No matching tasks", systemImage: "checklist")
                } description: {
                    Text(filter == TaskFilter() ? "Create a task, or let the assistant propose some from a page." : "Try different filters.")
                } actions: {
                    Button("New Task") { app.newTask() }
                }
            } else {
                table
            }
        }
        .navigationTitle("Tasks")
        .toolbar {
            ToolbarItem(placement: .primaryAction) {
                Button { app.newTask() } label: { Label("New Task", systemImage: "plus") }
                    .help("New Task")
            }
        }
        .onAppear(perform: reload)
        .onChange(of: app.revision) { reload() }
        .onChange(of: filter) { reload() }
        .confirmationDialog(
            "Delete “\(pendingDelete?.title ?? "")”?",
            isPresented: Binding(get: { pendingDelete != nil }, set: { if !$0 { pendingDelete = nil } }),
            titleVisibility: .visible,
            presenting: pendingDelete
        ) { task in
            Button("Delete Task", role: .destructive) { delete(task) }
            Button("Cancel", role: .cancel) {}
        } message: { _ in
            Text("This cannot be undone.")
        }
    }

    private var filterBar: some View {
        HStack(spacing: 12) {
            Picker("Status", selection: $status) {
                Text("All statuses").tag(String?.none)
                ForEach(TaskState.all, id: \.self) { s in Text(TaskState.label(s)).tag(Optional(s)) }
            }
            .fixedSize()
            Picker("Priority", selection: $priority) {
                Text("All priorities").tag(String?.none)
                ForEach(TaskPriorityLevel.all, id: \.self) { p in Text(TaskPriorityLevel.label(p)).tag(Optional(p)) }
            }
            .fixedSize()
            Picker("Due", selection: $due) {
                ForEach(DueFilter.allCases, id: \.self) { d in Text(d.label).tag(d) }
            }
            .fixedSize()
            Toggle("Hide done", isOn: $hideDone)
            Spacer()
            Text("\(tasks.count) task\(tasks.count == 1 ? "" : "s")").foregroundStyle(.secondary).font(.caption)
        }
    }

    private var table: some View {
        Table(tasks, selection: $selection) {
            TableColumn("") { task in
                Button {
                    setDone(task, !task.isDone)
                } label: {
                    Image(systemName: task.isDone ? "checkmark.circle.fill" : "circle")
                }
                .buttonStyle(.borderless)
                .help(task.isDone ? "Reopen" : "Complete")
            }
            .width(24)
            TableColumn("Title") { task in
                Text(task.title).strikethrough(task.isDone).help(task.description ?? "")
            }
            TableColumn("Status") { task in
                Text(TaskState.label(task.status))
            }
            .width(min: 70, ideal: 90)
            TableColumn("Priority") { task in
                Text(TaskPriorityLevel.label(task.priority))
                    .foregroundStyle(task.priority == TaskPriorityLevel.urgent ? Color.red : Color.primary)
            }
            .width(min: 60, ideal: 70)
            TableColumn("Due") { task in
                Text(task.dueDate ?? "—")
                    .foregroundStyle(isOverdue(task) ? Color.red : Color.primary)
            }
            .width(min: 80, ideal: 95)
            TableColumn("Page") { task in
                if let id = task.pageId, let page = app.page(id) {
                    Button(page.displayTitle) { app.open(pageId: id) }
                        .buttonStyle(.link)
                } else {
                    Text("—").foregroundStyle(.secondary)
                }
            }
        }
        .contextMenu(forSelectionType: String.self) { ids in
            if let task = tasks.first(where: { ids.contains($0.id) }) {
                Button("Edit…") { app.editTask(task) }
                Button(task.isDone ? "Reopen" : "Mark as Done") { setDone(task, !task.isDone) }
                if let pageId = task.pageId, app.page(pageId) != nil {
                    Button("Open Page") { app.open(pageId: pageId) }
                }
                Divider()
                Button("Delete…", role: .destructive) { pendingDelete = task }
            }
        } primaryAction: { ids in
            if let task = tasks.first(where: { ids.contains($0.id) }) { app.editTask(task) }
        }
    }

    private func isOverdue(_ task: TaskItem) -> Bool {
        guard let due = task.dueDate, !task.isDone else { return false }
        return due < SBClock.today()
    }

    private func reload() {
        guard let ws = app.workspace else { return }
        do {
            tasks = try ws.tasks.list(filter)
            selection = selection.filter { id in tasks.contains { $0.id == id } }
            error = nil
        } catch {
            self.error = describeError(error)
        }
    }

    private func setDone(_ task: TaskItem, _ done: Bool) {
        guard let ws = app.workspace else { return }
        do { try ws.tasks.setStatus(task.id, done ? TaskState.done : TaskState.todo) } catch { app.show("Could not update the task", error) }
    }

    private func delete(_ task: TaskItem) {
        guard let ws = app.workspace else { return }
        do { try ws.tasks.delete(task.id) } catch { app.show("Could not delete the task", error) }
    }
}

struct TaskEditorSheet: View {
    @EnvironmentObject private var app: AppModel
    @Environment(\.dismiss) private var dismiss
    let request: TaskEditorRequest

    @State private var title = ""
    @State private var details = ""
    @State private var status = TaskState.todo
    @State private var priority = TaskPriorityLevel.medium
    @State private var hasDue = false
    @State private var due = Date()
    @State private var pageId: String?
    @State private var error: String?
    @State private var loaded = false

    var body: some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(request.task == nil ? "New Task" : "Edit Task")
                .font(.headline)
                .padding([.top, .horizontal], 20)
            Form {
                TextField("Title", text: $title)
                TextField("Description", text: $details, axis: .vertical)
                    .lineLimit(3...8)
                Picker("Status", selection: $status) {
                    ForEach(TaskState.all, id: \.self) { s in Text(TaskState.label(s)).tag(s) }
                }
                Picker("Priority", selection: $priority) {
                    ForEach(TaskPriorityLevel.all, id: \.self) { p in Text(TaskPriorityLevel.label(p)).tag(p) }
                }
                Toggle("Due date", isOn: $hasDue)
                if hasDue {
                    DatePicker("Due", selection: $due, displayedComponents: .date)
                }
                Picker("Page", selection: $pageId) {
                    Text("None").tag(String?.none)
                    ForEach(app.allPages) { page in Text(page.displayTitle).tag(Optional(page.id)) }
                }
            }
            .formStyle(.grouped)
            if let error {
                Label(error, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(.red)
                    .padding(.horizontal, 20)
            }
            HStack {
                if let task = request.task, let pageId = task.pageId, app.page(pageId) != nil {
                    Button("Open Page") {
                        dismiss()
                        app.open(pageId: pageId)
                    }
                }
                Spacer()
                Button("Cancel", role: .cancel) { dismiss() }
                    .keyboardShortcut(.cancelAction)
                Button(request.task == nil ? "Create" : "Save", action: save)
                    .keyboardShortcut(.defaultAction)
                    .disabled(title.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty)
            }
            .padding(20)
        }
        .frame(width: 460)
        .onAppear(perform: populate)
    }

    private func populate() {
        guard !loaded else { return }
        loaded = true
        if let task = request.task {
            title = task.title
            details = task.description ?? ""
            status = task.status
            priority = task.priority
            if let date = SBClock.localDate(from: task.dueDate) {
                hasDue = true
                due = date
            }
            pageId = task.pageId
        } else {
            pageId = request.pageId
        }
    }

    private func save() {
        guard let ws = app.workspace else { return }
        let draft = TaskDraft(title, description: details, status: status, priority: priority,
                              dueDate: hasDue ? SBClock.localDateString(due) : nil, pageId: pageId)
        do {
            if let task = request.task {
                try ws.tasks.update(task.id, draft)
            } else {
                try ws.tasks.create(draft)
            }
            dismiss()
        } catch {
            self.error = describeError(error)
        }
    }
}
