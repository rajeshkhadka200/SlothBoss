import SwiftUI
import SlothBossCore

struct AssistantView: View {
    @EnvironmentObject private var app: AppModel
    @ObservedObject var assistant: AssistantModel
    let compact: Bool

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                ModelStatusRow(assistant: assistant)
                Spacer()
            }
            Picker("Action", selection: $assistant.mode) {
                ForEach(AssistantMode.allCases) { mode in
                    Label(mode.title, systemImage: mode.symbol).tag(mode)
                }
            }
            .pickerStyle(.menu)
            .disabled(assistant.isRunning)

            contextRow

            if !assistant.mode.needsPage {
                TextField(assistant.mode.placeholder, text: $assistant.input, axis: .vertical)
                    .lineLimit(compact ? 2...6 : 2...8)
                    .textFieldStyle(.roundedBorder)
                    .disabled(assistant.isRunning)
                    .onSubmit { assistant.run() }
            }

            HStack {
                Button {
                    assistant.run()
                } label: {
                    Label(assistant.mode.title, systemImage: assistant.mode.symbol)
                }
                .keyboardShortcut(.return, modifiers: [.command, .option])
                .disabled(!assistant.canRun)
                if assistant.isRunning {
                    ProgressView().controlSize(.small)
                    Button("Cancel", role: .cancel) { assistant.cancel() }
                }
                Spacer()
                if let elapsed = assistant.lastElapsed, elapsed > 0 {
                    Text(String(format: "%.1f s", elapsed)).font(.caption).foregroundStyle(.secondary)
                }
            }

            Divider()

            ScrollView {
                VStack(alignment: .leading, spacing: 14) {
                    output
                    HistorySection(assistant: assistant)
                }
                .frame(maxWidth: .infinity, alignment: .leading)
                .padding(.bottom, 12)
            }
        }
        .padding(compact ? 12 : 20)
        .frame(maxWidth: compact ? CGFloat.infinity : 760, alignment: .leading)
        .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        .onAppear { assistant.refreshHistory() }
    }

    @ViewBuilder
    private var contextRow: some View {
        if let page = assistant.contextPage {
            HStack(spacing: 4) {
                Image(systemName: "doc.text").foregroundStyle(.secondary)
                Text(assistant.mode.needsPage ? "Page: \(page.displayTitle)" : "Also using: \(page.displayTitle)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
                if !assistant.mode.needsPage {
                    Button {
                        app.contextPageId = nil
                    } label: {
                        Image(systemName: "xmark.circle.fill")
                    }
                    .buttonStyle(.borderless)
                    .foregroundStyle(.secondary)
                    .help("Don't include this page")
                }
            }
        } else if assistant.mode.needsPage {
            Text("Open a page to use this action.").font(.caption).foregroundStyle(.secondary)
        }
    }

    @ViewBuilder
    private var output: some View {
        if let error = assistant.errorMessage {
            Callout(text: error, symbol: "exclamationmark.triangle.fill", tint: .red)
        }
        if let result = assistant.askResult {
            AskResultView(result: result) { open($0) }
        }
        if !assistant.summaryText.isEmpty {
            VStack(alignment: .leading, spacing: 6) {
                Text("Summary").font(.headline)
                Text(assistant.summaryText).textSelection(.enabled)
            }
        }
        if let batch = assistant.batch {
            BatchView(assistant: assistant, batch: batch) { open($0) }
        }
        if assistant.isRunning && assistant.askResult == nil && assistant.summaryText.isEmpty && assistant.batch == nil {
            Text("Working locally… This can take a while on smaller machines.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }

    private func open(_ source: Source) {
        if source.isPage {
            app.open(pageId: source.refId)
        } else if let pageId = source.pageId {
            app.open(pageId: pageId)
        } else {
            app.editTask(id: source.refId)
        }
    }
}

struct ModelStatusRow: View {
    @ObservedObject var assistant: AssistantModel

    var body: some View {
        HStack(spacing: 6) {
            Circle().fill(color).frame(width: 8, height: 8)
            Text(text).font(.caption).foregroundStyle(.secondary).lineLimit(2)
            Button {
                Task { await assistant.refreshStatus() }
            } label: {
                Image(systemName: "arrow.clockwise")
            }
            .buttonStyle(.borderless)
            .help("Check the local model again")
            SettingsLink {
                Image(systemName: "gearshape")
            }
            .buttonStyle(.borderless)
            .help("Local AI settings")
        }
    }

    private var color: Color {
        switch assistant.status {
        case .ready: return .green
        case .checking, .unknown: return .gray
        case .noGemmaModels, .modelMissing: return .orange
        case .unavailable: return .red
        }
    }

    private var text: String {
        switch assistant.status {
        case .unknown: return "Local model not checked yet"
        case .checking: return "Checking Ollama…"
        case .ready(_, let model): return "\(model) · local"
        case .noGemmaModels: return "Ollama is running, but no Gemma model is installed. See Settings."
        case .modelMissing(_, let model): return "“\(model)” is not installed. Choose another model in Settings."
        case .unavailable(let message): return message
        }
    }
}

struct Callout: View {
    let text: String
    let symbol: String
    let tint: Color

    var body: some View {
        Label {
            Text(text).textSelection(.enabled)
        } icon: {
            Image(systemName: symbol).foregroundStyle(tint)
        }
        .font(.callout)
        .padding(8)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(tint.opacity(0.08), in: RoundedRectangle(cornerRadius: 6))
    }
}

struct CitationChips: View {
    let sources: [Source]
    let open: (Source) -> Void

    var body: some View {
        LazyVGrid(columns: [GridItem(.adaptive(minimum: 120), alignment: .leading)], alignment: .leading, spacing: 6) {
            ForEach(sources) { source in
                Button {
                    open(source)
                } label: {
                    Label("\(source.id) · \(source.displayTitle)", systemImage: source.isPage ? "doc.text" : "checkmark.circle")
                        .font(.caption)
                        .lineLimit(1)
                        .padding(.horizontal, 8)
                        .padding(.vertical, 3)
                        .background(Color.accentColor.opacity(0.12), in: Capsule())
                }
                .buttonStyle(.plain)
                .help("Open \(source.displayTitle)")
            }
        }
    }
}

struct AskResultView: View {
    let result: AskResult
    let open: (Source) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            HStack {
                Text("Answer").font(.headline)
                Text(statusLabel)
                    .font(.caption)
                    .padding(.horizontal, 6)
                    .padding(.vertical, 2)
                    .background(statusColor.opacity(0.15), in: Capsule())
                if !result.usedModel {
                    Text("model not called").font(.caption).foregroundStyle(.secondary)
                }
            }
            Text(result.answer).textSelection(.enabled)
            if !result.citations.isEmpty {
                Text("Sources").font(.subheadline.weight(.semibold))
                CitationChips(sources: result.citations, open: open)
            }
            if !result.inferences.isEmpty {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Model's own reasoning (not stated in your notes)").font(.subheadline.weight(.semibold))
                    ForEach(Array(result.inferences.enumerated()), id: \.offset) { _, inference in
                        Label(inference, systemImage: "lightbulb").font(.callout).foregroundStyle(.secondary)
                    }
                }
            }
            ForEach(Array(result.notices.enumerated()), id: \.offset) { _, notice in
                Callout(text: notice, symbol: "info.circle", tint: .orange)
            }
        }
    }

    private var statusLabel: String {
        switch result.status {
        case "answered": return "Answered from your notes"
        case "partial": return "Partly answered"
        default: return "Not found in your notes"
        }
    }

    private var statusColor: Color {
        switch result.status {
        case "answered": return .green
        case "partial": return .orange
        default: return .gray
        }
    }
}

struct BatchView: View {
    @ObservedObject var assistant: AssistantModel
    let batch: ProposalBatch
    let open: (Source) -> Void

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            if let summary = batch.summary, !summary.isEmpty {
                Text(summary).textSelection(.enabled)
            }
            if !batch.citations.isEmpty {
                CitationChips(sources: batch.citations, open: open)
            }
            ForEach(Array(batch.notices.enumerated()), id: \.offset) { _, notice in
                Callout(text: notice, symbol: "info.circle", tint: .orange)
            }
            if assistant.proposals.isEmpty {
                Text("No changes were proposed.").foregroundStyle(.secondary)
            } else {
                HStack {
                    Text("Proposed changes").font(.headline)
                    Spacer()
                    if assistant.proposals.contains(where: { $0.isPending }) {
                        Button("Approve All") { assistant.approveAll() }
                    }
                }
                Text("Nothing changes in your workspace until you approve.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                ForEach(assistant.proposals) { record in
                    ProposalRow(assistant: assistant, record: record)
                }
            }
        }
    }
}

struct ProposalRow: View {
    @ObservedObject var assistant: AssistantModel
    let record: ActionRecord

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(record.summary).font(.callout).textSelection(.enabled)
            HStack {
                StatusBadge(status: record.status)
                Spacer()
                if record.isPending {
                    Button("Reject") { assistant.reject(record.id) }
                    Button("Approve") { assistant.approve(record.id) }
                        .buttonStyle(.borderedProminent)
                } else if record.canUndo {
                    Button("Undo") { assistant.undo(record.id) }
                }
            }
            if let error = record.error {
                Text(error).font(.caption).foregroundStyle(.red)
            }
        }
        .padding(10)
        .background(Color.secondary.opacity(0.08), in: RoundedRectangle(cornerRadius: 8))
    }
}

struct StatusBadge: View {
    let status: String

    var body: some View {
        Text(label)
            .font(.caption)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .background(color.opacity(0.15), in: Capsule())
    }

    private var label: String {
        switch status {
        case ActionStatus.proposed: return "Proposed"
        case ActionStatus.applied: return "Applied"
        case ActionStatus.rejected: return "Rejected"
        case ActionStatus.failed: return "Failed"
        case ActionStatus.undone: return "Undone"
        default: return status
        }
    }

    private var color: Color {
        switch status {
        case ActionStatus.applied: return .green
        case ActionStatus.failed: return .red
        case ActionStatus.proposed: return .blue
        default: return .gray
        }
    }
}

struct HistorySection: View {
    @ObservedObject var assistant: AssistantModel
    @State private var expanded = false

    var body: some View {
        DisclosureGroup(isExpanded: $expanded) {
            VStack(alignment: .leading, spacing: 8) {
                if let error = assistant.historyError {
                    Callout(text: error, symbol: "exclamationmark.triangle.fill", tint: .red)
                }
                if assistant.history.isEmpty {
                    Text("No AI actions yet.").foregroundStyle(.secondary)
                }
                ForEach(assistant.history) { record in
                    VStack(alignment: .leading, spacing: 4) {
                        HStack {
                            StatusBadge(status: record.status)
                            Text(String(record.createdAt.prefix(16)).replacingOccurrences(of: "T", with: " "))
                                .font(.caption)
                                .foregroundStyle(.secondary)
                            Spacer()
                            if record.canUndo {
                                Button("Undo") { assistant.undo(record.id) }
                            }
                        }
                        Text(record.summary).font(.caption).lineLimit(3)
                        if let error = record.error {
                            Text(error).font(.caption).foregroundStyle(.red)
                        }
                    }
                    Divider()
                }
            }
            .padding(.top, 6)
        } label: {
            Text("AI action history").font(.headline)
        }
    }
}
