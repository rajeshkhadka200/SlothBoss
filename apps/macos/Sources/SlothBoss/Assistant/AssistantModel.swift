import Foundation
import SwiftUI
import SlothBossCore

enum AssistantMode: String, CaseIterable, Identifiable {
    case ask, summarize, extract, plan, draft

    var id: String { rawValue }

    var title: String {
        switch self {
        case .ask: return "Ask"
        case .summarize: return "Summarize Page"
        case .extract: return "Extract Tasks"
        case .plan: return "Plan Tasks"
        case .draft: return "Draft Note"
        }
    }

    var symbol: String {
        switch self {
        case .ask: return "questionmark.bubble"
        case .summarize: return "text.append"
        case .extract: return "checklist"
        case .plan: return "list.bullet.clipboard"
        case .draft: return "square.and.pencil"
        }
    }

    /// Works on the current page only (no free-text input).
    var needsPage: Bool { self == .summarize || self == .extract }

    var placeholder: String {
        switch self {
        case .ask: return "Ask a question about your notes…"
        case .plan: return "What should be reviewed? e.g. “Find unresolved questions in my research notes”"
        case .draft: return "What note should be drafted? e.g. “A brief on our Gemma latency results”"
        case .summarize, .extract: return ""
        }
    }
}

enum ModelStatus: Equatable {
    case unknown
    case checking
    case ready(version: String, model: String)
    case noGemmaModels(version: String)
    case modelMissing(version: String, model: String)
    case unavailable(String)

    var isReady: Bool { if case .ready = self { return true } else { return false } }
}

/// State and actions of the assistant pane. Every result shown comes from the local model via
/// AssistantService; nothing here fabricates answers.
@MainActor
final class AssistantModel: ObservableObject {
    static let defaultTimeout = 180

    weak var app: AppModel?

    @Published private(set) var status: ModelStatus = .unknown
    @Published private(set) var gemmaModels: [OllamaModel] = []
    @Published private(set) var ignoredModelCount = 0

    @Published var mode: AssistantMode = .ask
    @Published var input = ""
    @Published private(set) var isRunning = false
    @Published private(set) var errorMessage: String?
    @Published private(set) var askResult: AskResult?
    @Published private(set) var summaryText = ""
    @Published private(set) var batch: ProposalBatch?
    @Published private(set) var proposals: [ActionRecord] = []
    @Published private(set) var history: [ActionRecord] = []
    @Published private(set) var historyError: String?
    @Published private(set) var lastElapsed: TimeInterval?

    private var runTask: Task<Void, Never>?
    private var cachedClient: OllamaClient?
    private var cachedEndpoint: String?

    private var workspace: Workspace? { app?.workspace }

    // MARK: settings

    var endpointText: String {
        (try? workspace?.settings.get(SettingsStore.ollamaEndpoint)) ?? nil ?? OllamaClient.defaultEndpoint.absoluteString
    }

    var selectedModel: String? {
        guard let value = (try? workspace?.settings.get(SettingsStore.selectedModel)) ?? nil, !value.isEmpty else { return nil }
        return value
    }

    var timeoutSeconds: Int {
        let raw = (try? workspace?.settings.get(SettingsStore.aiTimeoutSeconds)) ?? nil
        return raw.flatMap { Int($0) } ?? AssistantModel.defaultTimeout
    }

    /// Validates (loopback only) and stores the endpoint.
    func setEndpoint(_ text: String) throws {
        let url = try OllamaClient.parseEndpoint(text)
        try workspace?.settings.set(SettingsStore.ollamaEndpoint, url.absoluteString)
        cachedClient = nil
        objectWillChange.send()
    }

    func selectModel(_ name: String) {
        guard !name.isEmpty else { return }
        do {
            try workspace?.settings.set(SettingsStore.selectedModel, name)
            objectWillChange.send()
            Task { await refreshStatus() }
        } catch {
            app?.show("Could not save the model choice", error)
        }
    }

    func setTimeout(_ seconds: Int) {
        do {
            try workspace?.settings.set(SettingsStore.aiTimeoutSeconds, String(seconds))
            objectWillChange.send()
        } catch {
            app?.show("Could not save the timeout", error)
        }
    }

    private func client() throws -> OllamaClient {
        let endpoint = endpointText
        if let cachedClient, cachedEndpoint == endpoint {
            cachedClient.timeout = TimeInterval(timeoutSeconds)
            return cachedClient
        }
        let client = try OllamaClient(endpoint: try OllamaClient.parseEndpoint(endpoint))
        client.timeout = TimeInterval(timeoutSeconds)
        cachedClient = client
        cachedEndpoint = endpoint
        return client
    }

    // MARK: status

    /// Checks /api/version and /api/tags. Only Gemma models are offered; nothing is ever downloaded.
    func refreshStatus() async {
        status = .checking
        do {
            let client = try self.client()
            let version = try await client.version()
            let all = try await client.listModels()
            gemmaModels = all.filter(\.isGemma)
            ignoredModelCount = all.count - gemmaModels.count
            if gemmaModels.isEmpty {
                status = .noGemmaModels(version: version)
                return
            }
            if let selected = selectedModel {
                status = gemmaModels.contains(where: { $0.name == selected })
                    ? .ready(version: version, model: selected)
                    : .modelMissing(version: version, model: selected)
            } else {
                let first = gemmaModels[0].name
                try workspace?.settings.set(SettingsStore.selectedModel, first)
                status = .ready(version: version, model: first)
            }
        } catch {
            gemmaModels = []
            ignoredModelCount = 0
            status = .unavailable(describeError(error))
        }
    }

    // MARK: running

    var contextPage: PageSummary? { app?.contextPageId.flatMap { app?.page($0) } }

    var canRun: Bool {
        guard !isRunning, workspace != nil else { return false }
        if mode.needsPage { return contextPage != nil }
        return !input.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty
    }

    private func clearOutputs() {
        errorMessage = nil
        askResult = nil
        summaryText = ""
        batch = nil
        proposals = []
        lastElapsed = nil
    }

    func run() {
        guard canRun, let ws = workspace else { return }
        guard let model = selectedModel else {
            errorMessage = "Choose a Gemma model in Settings first (SlothBoss → Settings → Local AI)."
            return
        }
        let client: OllamaClient
        do { client = try self.client() } catch {
            errorMessage = describeError(error)
            return
        }
        app?.activeEditor?.flush()
        clearOutputs()
        isRunning = true
        let service = AssistantService(workspace: ws, client: client, model: model)
        let mode = self.mode
        let text = input
        let pageId = app?.contextPageId
        let onDelta: @Sendable (String) -> Void = { [weak self] delta in
            DispatchQueue.main.async { [weak self] in
                MainActor.assumeIsolated { self?.appendStream(delta) }
            }
        }
        runTask = Task { [weak self] in
            do {
                switch mode {
                case .ask:
                    let result = try await service.ask(text, pinnedPageId: pageId)
                    self?.askResult = result
                    self?.lastElapsed = result.elapsed
                case .summarize:
                    guard let pageId else { throw SlothBossError("Open a page first.") }
                    let summary = try await service.summarize(pageId: pageId, onDelta: onDelta)
                    self?.summaryText = summary
                case .extract:
                    guard let pageId else { throw SlothBossError("Open a page first.") }
                    let result = try await service.extractTasks(pageId: pageId)
                    self?.show(result)
                case .plan:
                    let result = try await service.planTasks(text, pinnedPageId: pageId)
                    self?.show(result)
                case .draft:
                    let result = try await service.draftNote(text, pinnedPageId: pageId)
                    self?.show(result)
                }
            } catch is CancellationError {
                self?.errorMessage = "Cancelled. Nothing was changed."
            } catch {
                self?.errorMessage = describeError(error)
                if let e = error as? SlothBossError, e.kind == .ollamaUnavailable || e.kind == .modelNotFound {
                    await self?.refreshStatus()
                }
            }
            self?.isRunning = false
            self?.refreshHistory()
        }
    }

    private func appendStream(_ delta: String) {
        guard isRunning, mode == .summarize else { return }
        summaryText += delta
    }

    private func show(_ result: ProposalBatch) {
        batch = result
        proposals = result.proposals
        lastElapsed = result.elapsed
    }

    func cancel() {
        runTask?.cancel()
    }

    // MARK: proposals and history

    func approve(_ id: String) {
        guard let ws = workspace else { return }
        app?.activeEditor?.flush()
        do {
            let record = try ws.actions.approve(id)
            if record.status == ActionStatus.failed {
                errorMessage = "The change could not be applied: \(record.error ?? "unknown error"). Nothing was changed."
            }
        } catch {
            errorMessage = describeError(error)
        }
        reloadProposals()
    }

    func reject(_ id: String) {
        guard let ws = workspace else { return }
        do { try ws.actions.reject(id) } catch { errorMessage = describeError(error) }
        reloadProposals()
    }

    func approveAll() {
        for record in proposals where record.isPending { approve(record.id) }
    }

    func undo(_ id: String) {
        guard let ws = workspace else { return }
        app?.activeEditor?.flush()
        do {
            try ws.actions.undo(id)
            historyError = nil
        } catch {
            historyError = describeError(error)
        }
        refreshHistory()
        reloadProposals()
    }

    private func reloadProposals() {
        guard let ws = workspace, let batch else { return }
        proposals = (try? ws.actions.batch(batch.batchId)) ?? proposals
    }

    func refreshHistory() {
        guard let ws = workspace else { return }
        do {
            history = try ws.actions.history(limit: 100)
        } catch {
            historyError = describeError(error)
        }
    }

    func workspaceChanged() {
        refreshHistory()
        reloadProposals()
    }
}
