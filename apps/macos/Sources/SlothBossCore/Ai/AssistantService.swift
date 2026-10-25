import Foundation

public enum Prompts {
    public static func system() throws -> String { try ContractResources.read("prompts/system.md") }

    private static let placeholderRx = RegexPattern(#"\{\{(\w+)\}\}"#)

    /// Fills {{name}} placeholders in a single pass, so substituted values are never re-expanded.
    public static func render(_ name: String, _ values: [String: String]) throws -> String {
        let template = try ContractResources.read("prompts/\(name).md")
        return renderTemplate(template, values)
    }

    static func renderTemplate(_ template: String, _ values: [String: String]) -> String {
        placeholderRx.replace(template) { groups in values[groups[1]] ?? groups[0] }
    }

    /// The `$defs` entry of contracts/schemas/model-output.schema.json sent as Ollama's `format`.
    public static func outputSchema(_ name: String) throws -> JSON {
        let text = try ContractResources.read("schemas/model-output.schema.json")
        guard let schema = try JSON.parse(text)["$defs"]?[name] else {
            throw SlothBossError("The output schema '\(name)' is missing from the bundled contracts.")
        }
        return schema
    }
}

public struct AskResult: Sendable {
    public let status: String
    public let answer: String
    public let citations: [Source]
    public let inferences: [String]
    public let evidence: Evidence
    public let invalidCitations: Int
    public let notices: [String]
    public let usedModel: Bool
    public let elapsed: TimeInterval
}

public struct ProposalBatch: Sendable {
    public let batchId: String
    public let proposals: [ActionRecord]
    public let notices: [String]
    public let summary: String?
    public let citations: [Source]
    public let evidence: Evidence
    public let elapsed: TimeInterval
}

/// The AI features. Every method retrieves local evidence, calls the local model, validates the
/// output, and either returns text or stores *proposals* — it never mutates pages or tasks itself.
public final class AssistantService: @unchecked Sendable {
    public static let maxAttempts = 3

    public let model: String
    private let ws: Workspace
    private let client: OllamaClient
    private let retriever: Retriever

    public init(workspace: Workspace, client: OllamaClient, model: String) {
        self.ws = workspace
        self.client = client
        self.model = model
        self.retriever = Retriever(workspace)
    }

    private func structured<T>(_ userPrompt: String, schema: String, parse: (String) throws -> T) async throws -> (T, TimeInterval) {
        var messages = [ChatMessage("system", try Prompts.system()), ChatMessage("user", userPrompt)]
        let format = try Prompts.outputSchema(schema)
        var elapsed: TimeInterval = 0
        var lastError = ""
        for _ in 1...AssistantService.maxAttempts {
            let reply = try await client.chat(ChatRequest(model: model, messages: messages, format: format))
            elapsed += reply.elapsed
            do {
                return (try parse(reply.content), elapsed)
            } catch let e as SlothBossError where e.kind == .modelOutput {
                lastError = e.message
                messages.append(ChatMessage("assistant", reply.content.count > 2000 ? reply.content.prefixString(2000) : reply.content))
                messages.append(ChatMessage("user", "That reply was invalid: \(e.message) Reply again with only the corrected JSON object."))
            }
        }
        throw SlothBossError.modelOutput(
            "The model did not return a valid response after \(AssistantService.maxAttempts) attempts (\(lastError)). Nothing was changed.")
    }

    // MARK: Ask

    public func ask(_ rawQuestion: String, pinnedPageId: String? = nil) async throws -> AskResult {
        let question = rawQuestion.trimmed
        if question.isEmpty { throw SlothBossError("Type a question first.") }
        let evidence = try retriever.retrieve(question, pinnedPageId: pinnedPageId)
        if evidence.isEmpty {
            return AskResult(
                status: "not_found",
                answer: "I couldn't find anything in your workspace related to that question, so I can't answer it from your notes.",
                citations: [], inferences: [], evidence: evidence, invalidCitations: 0,
                notices: ["No matching pages or tasks were found; the model was not called."], usedModel: false, elapsed: 0)
        }

        let prompt = try Prompts.render("ask", ["evidence": evidence.format(), "question": question])
        let (output, elapsed) = try await structured(prompt, schema: "ask", parse: ModelOutput.parseAsk)

        let check = ModelOutput.validateCitations(output.answer, declared: output.citations, evidence: evidence)
        var notices: [String] = []
        var status = output.status
        if check.invalidCount > 0 {
            notices.append("Removed \(check.invalidCount) citation(s) to sources that were not retrieved.")
        }
        if status != "not_found" && check.validIds.isEmpty {
            notices.append("The model did not cite any retrieved source. Treat this answer as unverified.")
            if status == "answered" { status = "partial" }
        }
        if evidence.truncated { notices.append("Some matching material did not fit in the model's context.") }
        let cited = check.validIds.compactMap { evidence.find($0) }
        return AskResult(status: status, answer: check.text, citations: cited, inferences: output.inferences, evidence: evidence,
                         invalidCitations: check.invalidCount, notices: notices, usedModel: true, elapsed: elapsed)
    }

    // MARK: Summarize

    public func summarize(pageId: String, onDelta: (@Sendable (String) -> Void)? = nil) async throws -> String {
        let evidence = try retriever.forPage(pageId)
        if evidence.sources[0].excerpt.trimmed.isEmpty { return "This page is empty, so there is nothing to summarize." }
        let prompt = try Prompts.render("summarize", ["evidence": evidence.format()])
        let reply = try await client.chat(ChatRequest(model: model, messages: [ChatMessage("system", try Prompts.system()), ChatMessage("user", prompt)]),
                                          onDelta: onDelta)
        let text = reply.content.trimmed
        if text.isEmpty { throw SlothBossError.modelOutput("The model returned an empty summary.") }
        return evidence.truncated ? text + "\n\n(Only the first part of this long page was summarized.)" : text
    }

    // MARK: Extract tasks

    public func extractTasks(pageId: String) async throws -> ProposalBatch {
        guard let page = try ws.pages.get(pageId) else { throw SlothBossError("The page no longer exists.") }
        let evidence = try retriever.forPage(pageId)
        let batchId = Ids.new()
        if page.contentText.trimmed.isEmpty {
            return ProposalBatch(batchId: batchId, proposals: [], notices: ["This page is empty."], summary: nil, citations: [],
                                 evidence: evidence, elapsed: 0)
        }

        let prompt = try Prompts.render("extract_tasks", ["evidence": evidence.format(), "today": SBClock.today()])
        let (output, elapsed) = try await structured(prompt, schema: "extract_tasks", parse: ModelOutput.parseExtract)

        var notices: [String] = []
        var existing = Set(try ws.tasks.forPage(pageId).map { AssistantService.normalize($0.title) })
        let pageText = AssistantService.normalize(page.contentText)
        var proposals: [ActionRecord] = []
        for t in output.tasks.prefix(8) {
            guard let payload = try buildTask(title: t.title, description: t.description, priority: t.priority, due: t.dueDate,
                                              pageId: pageId, notices: &notices) else { continue }
            if !existing.insert(AssistantService.normalize(payload.title)).inserted {
                notices.append("Skipped “\(payload.title)”: a task with that title already exists for this page.")
                continue
            }
            let quote = t.evidence.trimmed.trimming(["\"", "“", "”"])
            let verified = !quote.isEmpty && pageText.contains(AssistantService.normalize(quote))
            let summary = AssistantService.describe(payload, pageTitle: page.displayTitle) +
                (quote.isEmpty ? "\nNo supporting quote given." :
                 verified ? "\nFrom the page: “\(AssistantService.clip(quote, 160))”" :
                 "\nModel quote not found on the page: “\(AssistantService.clip(quote, 160))”")
            proposals.append(try ws.actions.propose(batchId: batchId, .createTask(payload), summary: summary, model: model))
        }
        if output.tasks.isEmpty { notices.append("The model found no actionable tasks on this page.") }
        return ProposalBatch(batchId: batchId, proposals: proposals, notices: notices, summary: nil, citations: [],
                             evidence: evidence, elapsed: elapsed)
    }

    // MARK: Plan tasks across the workspace

    public func planTasks(_ rawRequest: String, pinnedPageId: String? = nil) async throws -> ProposalBatch {
        let request = rawRequest.trimmed
        if request.isEmpty { throw SlothBossError("Describe what you want reviewed first.") }
        let evidence = try retriever.retrieve(request, pinnedPageId: pinnedPageId)
        let batchId = Ids.new()
        if evidence.isEmpty {
            return ProposalBatch(batchId: batchId, proposals: [], notices: ["No matching pages or tasks were found; the model was not called."],
                                 summary: "I couldn't find notes related to that request.", citations: [], evidence: evidence, elapsed: 0)
        }

        let prompt = try Prompts.render("plan_tasks", ["evidence": evidence.format(), "question": request])
        let (output, elapsed) = try await structured(prompt, schema: "plan_tasks", parse: ModelOutput.parsePlan)

        var notices: [String] = []
        let check = ModelOutput.validateCitations(output.summary, declared: [], evidence: evidence)
        if check.invalidCount > 0 { notices.append("Removed \(check.invalidCount) citation(s) to sources that were not retrieved.") }

        var proposals: [ActionRecord] = []
        var titles = Set<String>()
        for t in output.tasks.prefix(8) {
            let source = evidence.find(t.source.trimming(["[", "]", " "]))
            if source == nil {
                notices.append("“\(AssistantService.clip(t.title, 60))” cited an unknown source (\(t.source.isEmpty ? "none" : t.source)), so it is not linked to a page.")
            }
            let description = t.rationale.isBlank ? t.description : "\(t.description)\nWhy: \(t.rationale)"
            guard let payload = try buildTask(title: t.title, description: description, priority: t.priority, due: "",
                                              pageId: source?.pageId, notices: &notices),
                  titles.insert(AssistantService.normalize(payload.title)).inserted else { continue }
            var linkedTitle: String?
            if let pageId = source?.pageId { linkedTitle = try ws.pages.get(pageId)?.displayTitle }
            let summary = AssistantService.describe(payload, pageTitle: linkedTitle) + (source.map { "\nSource: [\($0.id)] \($0.displayTitle)" } ?? "")
            proposals.append(try ws.actions.propose(batchId: batchId, .createTask(payload), summary: summary, model: model))
        }
        let cited = check.validIds.compactMap { evidence.find($0) }
        return ProposalBatch(batchId: batchId, proposals: proposals, notices: notices, summary: check.text, citations: cited,
                             evidence: evidence, elapsed: elapsed)
    }

    // MARK: Draft a note

    public func draftNote(_ rawRequest: String, pinnedPageId: String? = nil) async throws -> ProposalBatch {
        let request = rawRequest.trimmed
        if request.isEmpty { throw SlothBossError("Describe the note you want drafted first.") }
        let evidence = try retriever.retrieve(request, pinnedPageId: pinnedPageId)
        let batchId = Ids.new()
        if evidence.isEmpty {
            return ProposalBatch(batchId: batchId, proposals: [], notices: ["No matching pages or tasks were found; the model was not called."],
                                 summary: "I couldn't find material for that note in your workspace.", citations: [], evidence: evidence, elapsed: 0)
        }

        let prompt = try Prompts.render("draft_note", ["evidence": evidence.format(), "question": request])
        let (output, elapsed) = try await structured(prompt, schema: "draft_note", parse: ModelOutput.parseDraft)

        var notices: [String] = []
        let check = ModelOutput.validateCitations(output.markdown, declared: output.citations, evidence: evidence)
        if check.invalidCount > 0 { notices.append("Removed \(check.invalidCount) citation(s) to sources that were not retrieved.") }
        var doc = Markdown.parse(check.text)
        AssistantService.linkCitationMarkers(&doc, evidence)

        var citedPages: [Source] = []
        var seenPageIds = Set<String>()
        for id in check.validIds {
            if let s = evidence.find(id), let pageId = s.pageId, seenPageIds.insert(pageId).inserted { citedPages.append(s) }
        }
        if !citedPages.isEmpty {
            doc.blocks.append(Block.heading(2, "Sources"))
            for s in citedPages {
                guard let pageId = s.pageId else { continue }
                let title = try ws.pages.get(pageId)?.displayTitle ?? s.displayTitle
                doc.blocks.append(Block(.bulleted, [TextSpan("[\(s.id)] "), TextSpan(title, link: Links.forPage(pageId))]))
            }
        } else {
            notices.append("The draft does not cite any retrieved source.")
        }

        doc.normalize()
        let title = AssistantService.clip(output.title.replacingOccurrences(of: "\n", with: " "), PageStore.maxTitleLength)
        let payload = CreatePagePayload(title: title, parentId: nil, content: doc)
        let summary = "Create page “\(title)” (\(doc.blocks.count) blocks, \(citedPages.count) linked source page(s))"
        let proposal = try ws.actions.propose(batchId: batchId, .createPage(payload), summary: summary, model: model)
        return ProposalBatch(batchId: batchId, proposals: [proposal], notices: notices, summary: nil,
                             citations: check.validIds.compactMap { evidence.find($0) }, evidence: evidence, elapsed: elapsed)
    }

    private static let citationMarkerRx = RegexPattern(#"\[(S\d+)\]"#)

    /// Turns validated [S1] markers in drafted text into links to the source page.
    static func linkCitationMarkers(_ doc: inout PageDocument, _ evidence: Evidence) {
        for bi in doc.blocks.indices where doc.blocks[bi].type != .code {
            var spans: [TextSpan] = []
            for span in doc.blocks[bi].spans {
                if span.link != nil || span.code { spans.append(span); continue }
                let ns = span.text as NSString
                var last = 0
                for m in citationMarkerRx.matches(span.text) {
                    let id = ns.substring(with: m.range(at: 1))
                    guard let pageId = evidence.find(id)?.pageId else { continue }
                    if m.range.location > last {
                        spans.append(TextSpan(ns.substring(with: NSRange(location: last, length: m.range.location - last)),
                                          bold: span.bold, italic: span.italic))
                    }
                    spans.append(TextSpan(ns.substring(with: m.range), link: Links.forPage(pageId)))
                    last = m.range.location + m.range.length
                }
                if last < ns.length {
                    spans.append(TextSpan(ns.substring(from: last), bold: span.bold, italic: span.italic))
                }
            }
            doc.blocks[bi].spans = spans
        }
    }

    // MARK: helpers

    private func buildTask(title rawTitle: String, description: String, priority: String, due: String, pageId rawPageId: String?,
                           notices: inout [String]) throws -> CreateTaskPayload? {
        var title = rawTitle.replacingOccurrences(of: "\n", with: " ").trimmed.trimmingEnd(["."])
        if title.isEmpty { notices.append("Skipped a task with an empty title."); return nil }
        if title.count > TaskStore.maxTitleLength { title = title.prefixString(TaskStore.maxTitleLength) }
        var pr = priority.trimmed.lowercased()
        if !TaskPriorityLevel.isValid(pr) { pr = TaskPriorityLevel.medium }
        var dueDate: String? = due.trimmed
        if dueDate?.isEmpty == true {
            dueDate = nil
        } else if !SBClock.isValidDate(dueDate) {
            notices.append("Ignored an invalid due date “\(AssistantService.clip(dueDate ?? "", 20))” for “\(AssistantService.clip(title, 60))”.")
            dueDate = nil
        }
        let desc: String? = description.isBlank ? nil : AssistantService.clip(description.trimmed, TaskStore.maxDescriptionLength)
        var pageId = rawPageId
        if let p = pageId, try !ws.pages.exists(p) { pageId = nil }
        let payload = CreateTaskPayload(title: title, description: desc, priority: pr, dueDate: dueDate, pageId: pageId)
        do {
            try payload.validate()
            return payload
        } catch let e as SlothBossError {
            notices.append("Skipped an invalid task proposal: \(e.message)")
            return nil
        }
    }

    static func describe(_ p: CreateTaskPayload, pageTitle: String?) -> String {
        var parts = ["Create task “\(p.title)”", "\(TaskPriorityLevel.label(p.priority)) priority"]
        if let due = p.dueDate { parts.append("due \(due)") }
        parts.append(pageTitle.map { "linked to “\($0)”" } ?? "not linked to a page")
        return parts.joined(separator: " · ")
    }

    private static let whitespaceRx = RegexPattern(#"\s+"#)

    static func normalize(_ s: String) -> String { whitespaceRx.replace(s.lowercased(), template: " ").trimmed }

    static func clip(_ s: String, _ max: Int) -> String { s.count <= max ? s : s.prefixString(max - 1) + "…" }
}
