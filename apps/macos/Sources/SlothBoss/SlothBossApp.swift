import AppKit
import SwiftUI
import SlothBossCore

@main
struct SlothBossApp: App {
    @StateObject private var model = AppModel()

    var body: some Scene {
        WindowGroup("SlothBoss") {
            ContentView()
                .environmentObject(model)
                .frame(minWidth: 860, minHeight: 540)
        }
        .commands {
            SlothBossCommands(model: model)
        }

        Settings {
            SettingsView(assistant: model.assistant)
                .environmentObject(model)
        }
    }
}

struct SlothBossCommands: Commands {
    @ObservedObject var model: AppModel

    var body: some Commands {
        CommandGroup(replacing: .newItem) {
            Button("New Page") { model.newPage(parentId: nil) }
                .keyboardShortcut("n", modifiers: .command)
                .disabled(model.workspace == nil)
            Button("New Subpage") { model.newSubpageOfCurrent() }
                .keyboardShortcut("n", modifiers: [.command, .shift])
                .disabled(model.currentPageId == nil)
            Button("New Task") { model.newTask(pageId: model.currentPageId) }
                .keyboardShortcut("n", modifiers: [.command, .option])
                .disabled(model.workspace == nil)
        }

        CommandGroup(replacing: .importExport) {
            Button("Export Page as Markdown…") {
                if let id = model.currentPageId { model.exportMarkdown(pageId: id) }
            }
            .keyboardShortcut("e", modifiers: [.command, .shift])
            .disabled(model.currentPageId == nil)
            Divider()
            Button("Export Backup…") { model.exportBackup() }
                .disabled(model.workspace == nil)
            Button("Restore Backup…") { model.restoreBackup() }
                .disabled(model.workspace == nil)
        }

        CommandMenu("Go") {
            Button("Home") { model.selection = .home }
                .keyboardShortcut("h", modifiers: [.command, .shift])
            Button("Search") { model.focusSearch() }
                .keyboardShortcut("f", modifiers: [.command, .shift])
            Button("Tasks") { model.selection = .tasks }
                .keyboardShortcut("t", modifiers: [.command, .shift])
            Button("Archive") { model.selection = .archive }
            Divider()
            Button(model.showAssistantInspector ? "Hide Assistant" : "Show Assistant") { model.toggleAssistant() }
                .keyboardShortcut("j", modifiers: .command)
        }
    }
}
