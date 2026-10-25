import Foundation
import SwiftUI
import SlothBossCore

/// Editing state for one open page: debounced autosave of title and content, plus the related
/// lists (breadcrumbs, linked tasks, links, subpages) shown around the editor.
@MainActor
final class PageEditorModel: ObservableObject {
    enum SaveState: Equatable {
        case saved
        case pending
        case saving
        case failed(String)
    }

    static let autosaveDelay: UInt64 = 800_000_000 // nanoseconds

    let pageId: String
    weak var app: AppModel?
    let controller = EditorController()

    @Published var title = ""
    @Published private(set) var document = PageDocument()
    /// Bumped when the content is replaced from outside the editor (e.g. an approved AI draft).
    @Published private(set) var revision = 0
    @Published private(set) var saveState: SaveState = .saved
    @Published private(set) var loadError: String?
    @Published private(set) var isFavorite = false
    @Published private(set) var breadcrumb: [PageSummary] = []
    @Published private(set) var tasks: [TaskItem] = []
    @Published private(set) var backlinks: [PageSummary] = []
    @Published private(set) var outgoing: [PageSummary] = []
    @Published private(set) var children: [PageSummary] = []

    private var savedTitle = ""
    private var pendingDocument: PageDocument?
    private var lastKnownUpdatedAt = ""
    private var saveTask: Task<Void, Never>?

    init(pageId: String, app: AppModel) {
        self.pageId = pageId
        self.app = app
        load()
    }

    private var workspace: Workspace? { app?.workspace }

    func load() {
        guard let ws = workspace else { return }
        do {
            guard let page = try ws.pages.get(pageId) else {
                loadError = "This page no longer exists."
                return
            }
            title = page.title
            savedTitle = page.title
            isFavorite = page.isFavorite
            document = try page.document()
            lastKnownUpdatedAt = page.updatedAt
            loadError = nil
            reloadRelated()
        } catch {
            loadError = describeError(error)
        }
    }

    func reloadRelated() {
        guard let ws = workspace else { return }
        do {
            breadcrumb = try ws.pages.breadcrumb(pageId)
            tasks = try ws.tasks.forPage(pageId)
            backlinks = try ws.pages.backlinks(pageId)
            outgoing = try ws.pages.outgoingLinks(pageId)
            children = try ws.pages.children(pageId)
        } catch {
            // Related lists are secondary; keep the editor usable.
        }
    }

    // MARK: edits

    func documentChanged(_ doc: PageDocument) {
        pendingDocument = doc
        scheduleSave()
    }

    func titleChanged() {
        guard title != savedTitle else { return }
        scheduleSave()
    }

    private var hasPendingChanges: Bool { pendingDocument != nil || title != savedTitle }

    private func scheduleSave() {
        saveState = .pending
        saveTask?.cancel()
        saveTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: PageEditorModel.autosaveDelay)
            guard !Task.isCancelled else { return }
            self?.flush()
        }
    }

    /// Writes pending changes immediately (navigation, quit, export, restore).
    func flush() {
        saveTask?.cancel()
        saveTask = nil
        guard hasPendingChanges, let ws = workspace else {
            if case .pending = saveState { saveState = .saved }
            return
        }
        saveState = .saving
        do {
            if title != savedTitle {
                let newTitle = title
                try ws.pages.rename(pageId, newTitle)
                savedTitle = newTitle
            }
            if let doc = pendingDocument {
                pendingDocument = nil
                lastKnownUpdatedAt = try ws.pages.updateContent(pageId, doc)
                document = doc
            }
            if let page = try ws.pages.get(pageId) { lastKnownUpdatedAt = page.updatedAt }
            saveState = .saved
        } catch {
            saveState = .failed(describeError(error))
        }
    }

    /// Called after any workspace write. Reloads related lists, and the content if the page was
    /// changed elsewhere while there are no unsaved local edits.
    func workspaceChanged() {
        guard let ws = workspace else { return }
        reloadRelated()
        guard let page = try? ws.pages.get(pageId) else {
            loadError = "This page no longer exists."
            return
        }
        isFavorite = page.isFavorite
        guard !hasPendingChanges, page.updatedAt != lastKnownUpdatedAt else { return }
        lastKnownUpdatedAt = page.updatedAt
        if page.title != savedTitle {
            title = page.title
            savedTitle = page.title
        }
        if let doc = try? page.document(), doc != document {
            document = doc
            revision += 1
        }
    }

    func reloadTitleIfNeeded(pageId: String) {
        guard pageId == self.pageId, let ws = workspace, let page = try? ws.pages.get(pageId) else { return }
        title = page.title
        savedTitle = page.title
        lastKnownUpdatedAt = page.updatedAt
    }

    // MARK: actions

    func toggleFavorite() {
        guard let ws = workspace else { return }
        do { try ws.pages.setFavorite(pageId, !isFavorite) } catch { app?.show("Could not update favorites", error) }
    }

    func setTaskDone(_ task: TaskItem, _ done: Bool) {
        guard let ws = workspace else { return }
        do { try ws.tasks.setStatus(task.id, done ? TaskState.done : TaskState.todo) } catch { app?.show("Could not update the task", error) }
    }
}
