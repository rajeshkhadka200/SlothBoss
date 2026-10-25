import AppKit
import SwiftUI
import SlothBossCore

struct SettingsView: View {
    @EnvironmentObject private var app: AppModel
    @ObservedObject var assistant: AssistantModel
    @State private var endpoint = ""
    @State private var endpointMessage: String?
    @State private var endpointIsError = false
    @State private var timeout = AssistantModel.defaultTimeout

    private static let timeouts = [60, 120, 180, 300, 600, 900]

    var body: some View {
        Group {
            if app.workspace == nil {
                ContentUnavailableView("No workspace", systemImage: "exclamationmark.triangle",
                                       description: Text(app.openError ?? "The workspace is not open."))
            } else {
                form
            }
        }
        .frame(width: 560)
        .frame(minHeight: 420)
        .onAppear {
            endpoint = assistant.endpointText
            timeout = assistant.timeoutSeconds
            Task { await assistant.refreshStatus() }
        }
    }

    private var form: some View {
        Form {
            Section("Local AI (Ollama)") {
                HStack {
                    TextField("Endpoint", text: $endpoint, prompt: Text(OllamaClient.defaultEndpoint.absoluteString))
                    Button("Save") { saveEndpoint() }
                }
                if let message = endpointMessage {
                    Text(message).font(.caption).foregroundStyle(endpointIsError ? Color.red : Color.secondary)
                }
                Text("Only this Mac's own Ollama server (localhost, 127.0.0.1 or ::1) is allowed. Your notes never leave this Mac.")
                    .font(.caption)
                    .foregroundStyle(.secondary)

                LabeledContent("Status") {
                    HStack {
                        ModelStatusRow(assistant: assistant)
                        Spacer()
                    }
                }

                Picker("Model", selection: Binding(get: { assistant.selectedModel ?? "" }, set: { assistant.selectModel($0) })) {
                    if assistant.gemmaModels.isEmpty {
                        Text(assistant.selectedModel ?? "No Gemma models found").tag(assistant.selectedModel ?? "")
                    } else {
                        if let selected = assistant.selectedModel, !assistant.gemmaModels.contains(where: { $0.name == selected }) {
                            Text("\(selected) (not installed)").tag(selected)
                        }
                        ForEach(assistant.gemmaModels) { model in
                            Text(modelLabel(model)).tag(model.name)
                        }
                    }
                }
                .disabled(assistant.gemmaModels.isEmpty)
                if assistant.ignoredModelCount > 0 {
                    Text("\(assistant.ignoredModelCount) non-Gemma model\(assistant.ignoredModelCount == 1 ? " is" : "s are") installed and ignored. SlothBoss uses Gemma models only.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }

                Picker("Response timeout", selection: $timeout) {
                    ForEach(SettingsView.timeouts, id: \.self) { seconds in
                        Text(seconds < 120 ? "\(seconds) seconds" : "\(seconds / 60) minutes").tag(seconds)
                    }
                }
                .onChange(of: timeout) { if timeout != assistant.timeoutSeconds { assistant.setTimeout(timeout) } }
            }

            if needsSetup {
                Section("Setup") {
                    VStack(alignment: .leading, spacing: 8) {
                        Text("SlothBoss uses a Gemma model running in Ollama on this Mac. It never downloads anything by itself.")
                        Text("1. Install Ollama from ollama.com and start it.")
                        Text("2. In Terminal, download a Gemma model, for example:")
                        Text("ollama pull gemma4:e2b-it-qat")
                            .font(.system(.body, design: .monospaced))
                            .textSelection(.enabled)
                            .padding(6)
                            .background(Color.secondary.opacity(0.1), in: RoundedRectangle(cornerRadius: 4))
                        Text("Any Gemma model listed by “ollama list” works; smaller ones are faster on laptops.")
                        Text("3. Click the refresh button above.")
                    }
                    .font(.callout)
                }
            }

            Section("Workspace") {
                if let path = app.workspace?.path {
                    LabeledContent("File") {
                        Text(path).font(.caption).textSelection(.enabled).lineLimit(2)
                    }
                    Button("Show in Finder") {
                        NSWorkspace.shared.activateFileViewerSelecting([URL(fileURLWithPath: path)])
                    }
                }
                Text("Back up regularly with File → Export Backup….").font(.caption).foregroundStyle(.secondary)
            }
        }
        .formStyle(.grouped)
    }

    private var needsSetup: Bool {
        switch assistant.status {
        case .noGemmaModels, .unavailable, .modelMissing: return true
        default: return false
        }
    }

    private func modelLabel(_ model: OllamaModel) -> String {
        let details = [model.parameterSize, model.quantization, model.sizeLabel].filter { !$0.isEmpty }.joined(separator: " · ")
        return details.isEmpty ? model.name : "\(model.name)  (\(details))"
    }

    private func saveEndpoint() {
        do {
            try assistant.setEndpoint(endpoint)
            endpoint = assistant.endpointText
            endpointMessage = "Saved."
            endpointIsError = false
            Task { await assistant.refreshStatus() }
        } catch {
            endpointMessage = describeError(error)
            endpointIsError = true
        }
    }
}
