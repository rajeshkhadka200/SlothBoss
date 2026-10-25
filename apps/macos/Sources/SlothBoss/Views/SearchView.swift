import SwiftUI
import SlothBossCore

struct SearchView: View {
    @EnvironmentObject private var app: AppModel
    @State private var query = ""
    @State private var hits: [SearchHit] = []
    @State private var error: String?
    @FocusState private var fieldFocused: Bool

    var body: some View {
        VStack(spacing: 0) {
            HStack {
                Image(systemName: "magnifyingglass").foregroundStyle(.secondary)
                TextField("Search pages and tasks", text: $query)
                    .textFieldStyle(.plain)
                    .font(.title3)
                    .focused($fieldFocused)
                    .onSubmit { if let first = hits.first { open(first) } }
                if !query.isEmpty {
                    Button { query = "" } label: { Image(systemName: "xmark.circle.fill") }
                        .buttonStyle(.borderless)
                        .foregroundStyle(.secondary)
                }
            }
            .padding(12)
            Divider()
            results
        }
        .navigationTitle("Search")
        .onAppear {
            fieldFocused = true
            run()
        }
        .onChange(of: query) { run() }
        .onChange(of: app.revision) { run() }
        .onChange(of: app.searchFocusRequest) { fieldFocused = true }
    }

    @ViewBuilder
    private var results: some View {
        if let error {
            ContentUnavailableView("Search failed", systemImage: "exclamationmark.triangle", description: Text(error))
        } else if query.trimmingCharacters(in: .whitespaces).isEmpty {
            ContentUnavailableView("Search your workspace", systemImage: "magnifyingglass",
                                   description: Text("Type to search page titles, page text and tasks. Archived pages are not searched."))
        } else if hits.isEmpty {
            ContentUnavailableView.search(text: query)
        } else {
            List(hits) { hit in
                Button { open(hit) } label: {
                    VStack(alignment: .leading, spacing: 3) {
                        Label(hit.title.isEmpty ? "Untitled" : hit.title, systemImage: hit.isPage ? "doc.text" : "checkmark.circle")
                            .font(.headline)
                        if !hit.snippet.isEmpty {
                            Text(SearchView.highlight(hit.snippet))
                                .font(.callout)
                                .foregroundStyle(.secondary)
                                .lineLimit(3)
                        }
                    }
                    .padding(.vertical, 3)
                    .frame(maxWidth: .infinity, alignment: .leading)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
    }

    private func run() {
        guard let ws = app.workspace else { return }
        do {
            hits = try ws.search.query(query)
            error = nil
        } catch {
            hits = []
            self.error = describeError(error)
        }
    }

    private func open(_ hit: SearchHit) {
        if hit.isPage { app.open(pageId: hit.refId) } else { app.editTask(id: hit.refId) }
    }

    /// Renders FTS snippet markers (\u{2} … \u{3}) as emphasized text.
    static func highlight(_ snippet: String) -> AttributedString {
        var result = AttributedString()
        var current = ""
        var emphasized = false
        func flush() {
            guard !current.isEmpty else { return }
            var piece = AttributedString(current)
            if emphasized {
                piece.font = Font.callout.bold()
                piece.foregroundColor = Color.primary
                piece.backgroundColor = Color.yellow.opacity(0.3)
            }
            result += piece
            current = ""
        }
        for ch in snippet {
            if ch == SearchHit.matchStart {
                flush()
                emphasized = true
            } else if ch == SearchHit.matchEnd {
                flush()
                emphasized = false
            } else {
                current.append(ch)
            }
        }
        flush()
        return result
    }
}
