import AppKit
import Combine
import SwiftUI
import UniformTypeIdentifiers
import SlothBossCore

enum SidebarItem: Hashable {
    case home, search, tasks, assistant, archive
    case page(String)
}

struct PageNode: Identifiable, Hashable {
    let page: PageSummary
    var children: [PageNode]?
    var id: String { page.id }
}

struct AlertMessage: Identifiable {
    let id = UUID()
    let title: String
    let message: String
}

struct DeleteRequest: Identifiable {
    let page: PageSummary
    let pages: Int
    let linkedTasks: Int
    var id: String { page.id }

    var message: String {
        var parts: [String] = []
        if pages > 1 {
            parts.append("This also deletes \(pages - 1) subpage\(pages - 1 == 1 ? "" : "s").")
        }
        if linkedTasks > 0 {
            parts.append("\(linkedTasks) linked task\(linkedTasks == 1 ? " is" : "s are") kept but will no longer be linked to a page.")
        }
        parts.append("This cannot be undone. Consider archiving instead.")
        return parts.joined(separator: " ")
    }
}

struct TaskEditorRequest: Identifiable {
    let id = UUID()
    let task: TaskItem?
    let pageId: String?
}

/// App-wide state: the open workspace, sidebar data, navigation and workspace-level commands.
@MainActor
final class AppModel: ObservableObject {
    static let generator = "SlothBoss macOS 0.1.0"

    @Published private(set) var workspace: Workspace?
    @Published private(set) var openError: String?

    @Published var selection: SidebarItem? = .home {
        didSet {
            if oldValue != selection {
                activeEditor?.flush()
                if case .page(let id) = selection { contextPageId = id }
            }
        }
    }

    @Published private(set) var tree: [PageNode] = []
    @Published private(set) var allPages: [PageSummary] = []
    @Published private(set) var favorites: [PageSummary] = []
    @Published private(set) var archived: [PageSummary] = []
    /// Increments after every committed workspace write; views reload their data when it changes.
    @Published private(set) var revision = 0
    /// The page the assistant works on: the open page, or the last one opened.
    @Published var contextPageId: String?

    @Published var showAssistantInspector = false
    @Published var alert: AlertMessage?
    @Published var renameTarget: PageSummary?
    @Published var deleteRequest: DeleteRequest?
    @Published var taskEditor: TaskEditorRequest?
    @Published var searchFocusRequest = 0

    weak var activeEditor: PageEditorModel?
    let assistant: AssistantModel

    private var cancellables: Set<AnyCancellable> = []

    init() {
        assistant = AssistantModel()
        assistant.app = self
        openWorkspace()
        NotificationCenter.default.publisher(for: Workspace.didChangeNotification)
            .receive(on: DispatchQueue.main)
            .sink { [weak self] _ in self?.workspaceDidChange() }
            .store(in: &cancellables)
        NotificationCenter.default.publisher(for: NSApplication.willTerminateNotification)
            .sink { [weak self] _ in self?.activeEditor?.flush() }
            .store(in: &cancellables)
    }

    private func openWorkspace() {
        do {
            let path = try Workspace.defaultPath()
            workspace = try Workspace.open(path: path)
            reloadLists()
            Task { await assistant.refreshStatus() }
        } catch {
            openError = describeError(error)
        }
    }

    // MARK: data

    var currentPageId: String? {
        if case .page(let id) = selection { return id }
        return nil
    }

    func page(_ id: String) -> PageSummary? { allPages.first { $0.id == id } }

    private func workspaceDidChange() {
        reloadLists()
        revision += 1
        activeEditor?.workspaceChanged()
        assistant.workspaceChanged()
    }

    func reloadLists() {
        guard let ws = workspace else { return }
        do {
            allPages = try ws.pages.list()
            favorites = try ws.pages.favorites()
            archived = try ws.pages.archived()
            tree = AppModel.buildTree(allPages)
            if let id = contextPageId, !allPages.contains(where: { $0.id == id }) { contextPageId = nil }
            if case .page(let id) = selection, !allPages.contains(where: { $0.id == id }) { selection = .home }
        } catch {
            show("Could not load pages", error)
        }
    }

    static func buildTree(_ pages: [PageSummary]) -> [PageNode] {
        let ids = Set(pages.map(\.id))
        var byParent: [String: [PageSummary]] = [:]
        var roots: [PageSummary] = []
        for p in pages {
            if let parent = p.parentId, ids.contains(parent) { byParent[parent, default: []].append(p) } else { roots.append(p) }
        }
        func node(_ p: PageSummary, depth: Int) -> PageNode {
            let kids = depth > 64 ? [] : (byParent[p.id] ?? []).map { node($0, depth: depth + 1) }
            return PageNode(page: p, children: kids.isEmpty ? nil : kids)
        }
        return roots.map { node($0, depth: 0) }
    }

    func show(_ title: String, _ error: Error) {
        alert = AlertMessage(title: title, message: describeError(error))
    }

    func show(_ title: String, message: String) {
        alert = AlertMessage(title: title, message: message)
    }

    // MARK: page commands

    func newPage(parentId: String?) {
        guard let ws = workspace else { return }
        do {
            let page = try ws.pages.create("", parentId: parentId)
            reloadLists()
            selection = .page(page.id)
        } catch {
            show("Could not create the page", error)
        }
    }

    func newSubpageOfCurrent() {
        guard let id = currentPageId else { return }
        newPage(parentId: id)
    }

    func open(pageId: String) {
        if page(pageId) != nil {
            selection = .page(pageId)
        } else if let ws = workspace, let p = try? ws.pages.get(pageId), p.isArchived {
            show("Page is archived", message: "“\(p.displayTitle)” is in the archive. Restore it from Archive to open it.")
        } else {
            show("Page not found", message: "The linked page no longer exists.")
        }
    }

    func rename(_ page: PageSummary, to title: String) {
        guard let ws = workspace else { return }
        activeEditor?.flush()
        do {
            try ws.pages.rename(page.id, title)
            activeEditor?.reloadTitleIfNeeded(pageId: page.id)
        } catch {
            show("Could not rename the page", error)
        }
    }

    func toggleFavorite(_ page: PageSummary) {
        guard let ws = workspace else { return }
        do { try ws.pages.setFavorite(page.id, !page.isFavorite) } catch { show("Could not update favorites", error) }
    }

    func archive(_ pageId: String) {
        guard let ws = workspace else { return }
        activeEditor?.flush()
        do {
            try ws.pages.archive(pageId)
        } catch {
            show("Could not archive the page", error)
        }
    }

    func restore(_ pageId: String) {
        guard let ws = workspace else { return }
        do { try ws.pages.restore(pageId) } catch { show("Could not restore the page", error) }
    }

    func requestDelete(_ page: PageSummary) {
        guard let ws = workspace else { return }
        do {
            let impact = try ws.pages.deletionImpact(page.id)
            deleteRequest = DeleteRequest(page: page, pages: impact.pages, linkedTasks: impact.linkedTasks)
        } catch {
            show("Could not delete the page", error)
        }
    }

    func confirmDelete(_ request: DeleteRequest) {
        guard let ws = workspace else { return }
        activeEditor?.flush()
        do {
            try ws.pages.delete(request.page.id)
        } catch {
            show("Could not delete the page", error)
        }
    }

    func exportMarkdown(pageId: String) {
        guard let ws = workspace else { return }
        activeEditor?.flush()
        do {
            guard let page = try ws.pages.get(pageId) else { return show("Page not found", message: "The page no longer exists.") }
            let markdown = Markdown.exportPage(title: page.title, try page.document())
            let panel = NSSavePanel()
            panel.title = "Export Page as Markdown"
            panel.nameFieldStringValue = BackupService.safeFileName(page.title, extension: ".md")
            panel.allowedContentTypes = [UTType(filenameExtension: "md") ?? .plainText]
            panel.canCreateDirectories = true
            guard panel.runModal() == .OK, let url = panel.url else { return }
            try Data(markdown.utf8).write(to: url, options: [.atomic])
        } catch {
            show("Could not export the page", error)
        }
    }

    // MARK: navigation commands

    func focusSearch() {
        selection = .search
        searchFocusRequest += 1
    }

    func toggleAssistant() {
        showAssistantInspector.toggle()
    }

    func newTask(pageId: String? = nil) {
        taskEditor = TaskEditorRequest(task: nil, pageId: pageId)
    }

    func editTask(_ task: TaskItem) {
        taskEditor = TaskEditorRequest(task: task, pageId: task.pageId)
    }

    func editTask(id: String) {
        guard let ws = workspace else { return }
        do {
            if let task = try ws.tasks.get(id) { editTask(task) } else { show("Task not found", message: "The task no longer exists.") }
        } catch {
            show("Could not open the task", error)
        }
    }

    // MARK: backup

    func exportBackup() {
        guard let ws = workspace else { return }
        activeEditor?.flush()
        let panel = NSSavePanel()
        panel.title = "Export Backup"
        panel.nameFieldStringValue = "SlothBoss Backup \(AppModel.fileStamp()).json"
        panel.allowedContentTypes = [.json]
        panel.canCreateDirectories = true
        guard panel.runModal() == .OK, let url = panel.url else { return }
        do {
            try BackupService.exportToFile(ws, path: url.path, generator: AppModel.generator)
            show("Backup exported", message: "Saved to \(url.path).")
        } catch {
            show("Could not export the backup", error)
        }
    }

    /// Validates the whole file first, asks for confirmation, snapshots the current database next to
    /// it (VACUUM INTO a timestamped .bak), then replaces the workspace contents in one transaction.
    func restoreBackup() {
        guard let ws = workspace else { return }
        let panel = NSOpenPanel()
        panel.title = "Restore Backup"
        panel.allowedContentTypes = [.json]
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        guard panel.runModal() == .OK, let url = panel.url else { return }

        let backup: ValidatedBackup
        do {
            backup = try BackupService.validateFile(url.path)
        } catch {
            show("This backup can't be restored", error)
            return
        }

        let confirm = NSAlert()
        confirm.alertStyle = .warning
        confirm.messageText = "Replace this workspace with the backup?"
        var info = "The backup contains \(backup.pages.count) page(s) and \(backup.tasks.count) task(s)"
        if !backup.exportedAt.isEmpty { info += ", exported \(backup.exportedAt)" }
        if let generator = backup.generator { info += " by \(generator)" }
        info += ".\n\nAll current pages, tasks, links and AI history will be replaced. Settings are kept. "
        info += "A snapshot of the current workspace is saved next to it first."
        if !backup.warnings.isEmpty {
            info += "\n\nNotes:\n• " + backup.warnings.prefix(5).joined(separator: "\n• ")
            if backup.warnings.count > 5 { info += "\n• …and \(backup.warnings.count - 5) more" }
        }
        confirm.informativeText = info
        confirm.addButton(withTitle: "Replace Workspace")
        confirm.addButton(withTitle: "Cancel")
        guard confirm.runModal() == .alertFirstButtonReturn else { return }

        activeEditor?.flush()
        selection = .home
        let snapshotPath = ws.path + "." + AppModel.fileStamp() + ".bak"
        do {
            try ws.snapshot(to: snapshotPath)
        } catch {
            show("Restore cancelled", message: "The current workspace could not be snapshotted, so nothing was changed.\n\n\(describeError(error))")
            return
        }
        do {
            try BackupService.restoreReplacing(ws, backup)
            show("Backup restored", message: "The previous workspace was saved to \(snapshotPath).")
        } catch {
            show("Could not restore the backup", message: "Nothing was changed.\n\n\(describeError(error))")
        }
    }

    static func fileStamp() -> String {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyyMMdd-HHmmss"
        return f.string(from: Date())
    }
}
