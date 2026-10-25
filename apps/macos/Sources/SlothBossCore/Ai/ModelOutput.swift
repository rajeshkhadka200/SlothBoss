import Foundation

public struct AskOutput: Equatable, Sendable {
    public let status: String
    public let answer: String
    public let citations: [String]
    public let inferences: [String]
}

public struct ExtractedTask: Equatable, Sendable {
    public let title: String
    public let description: String
    public let priority: String
    public let dueDate: String
    public let evidence: String
}

public struct ExtractOutput: Equatable, Sendable {
    public let tasks: [ExtractedTask]
}

public struct PlannedTask: Equatable, Sendable {
    public let title: String
    public let description: String
    public let priority: String
    public let source: String
    public let rationale: String
}

public struct PlanOutput: Equatable, Sendable {
    public let summary: String
    public let tasks: [PlannedTask]
}

public struct DraftOutput: Equatable, Sendable {
    public let title: String
    public let markdown: String
    public let citations: [String]
}

public struct CitationCheck: Equatable, Sendable {
    public let text: String
    public let validIds: [String]
    public let invalidCount: Int
}

/// Parses and validates structured model responses. Constrained decoding (Ollama 'format') usually
/// produces valid JSON, but every response is still checked here; failures trigger a bounded retry.
public enum ModelOutput {
    public static let askStatuses = ["answered", "partial", "not_found"]

    /// The text between the first '{' and the last '}', parsed as a JSON object.
    public static func extractJSON(_ text: String) throws -> JSONObject {
        let t = text.trimmed
        guard let start = t.firstIndex(of: "{"), let end = t.lastIndex(of: "}"), start < end else {
            throw SlothBossError.modelOutput("The reply did not contain a JSON object.")
        }
        let slice = String(t[start...end])
        let node: JSON
        do { node = try JSON.parse(slice) } catch {
            throw SlothBossError.modelOutput("The reply was not valid JSON (\(error)).")
        }
        guard let o = node.objectValue else { throw SlothBossError.modelOutput("The reply was not a JSON object.") }
        return o
    }

    private static func str(_ o: JSONObject, _ key: String, required: Bool = true) throws -> String {
        guard let node = o[key], !node.isNull else {
            if required { throw SlothBossError.modelOutput("Missing field '\(key)'.") }
            return ""
        }
        guard let s = node.stringValue else { throw SlothBossError.modelOutput("Field '\(key)' must be a string.") }
        return s.trimmed
    }

    private static func strList(_ o: JSONObject, _ key: String) throws -> [String] {
        guard let node = o[key], !node.isNull else { return [] }
        guard let arr = node.arrayValue else { throw SlothBossError.modelOutput("Field '\(key)' must be a list.") }
        return try arr.map { item -> String in
            guard let s = item.stringValue else { throw SlothBossError.modelOutput("'\(key)' must contain strings.") }
            return s.trimmed
        }.filter { !$0.isEmpty }
    }

    private static func objList(_ o: JSONObject, _ key: String) throws -> [JSONObject] {
        guard let arr = o[key]?.arrayValue else { throw SlothBossError.modelOutput("Field '\(key)' must be a list.") }
        return try arr.map { item in
            guard let obj = item.objectValue else { throw SlothBossError.modelOutput("'\(key)' must contain objects.") }
            return obj
        }
    }

    public static func parseAsk(_ text: String) throws -> AskOutput {
        let o = try extractJSON(text)
        let status = try str(o, "status")
        if !askStatuses.contains(status) { throw SlothBossError.modelOutput("Unknown status '\(status)'.") }
        let answer = try str(o, "answer")
        if answer.isEmpty { throw SlothBossError.modelOutput("The answer was empty.") }
        return AskOutput(status: status, answer: answer, citations: try strList(o, "citations"), inferences: try strList(o, "inferences"))
    }

    public static func parseExtract(_ text: String) throws -> ExtractOutput {
        let o = try extractJSON(text)
        let tasks = try objList(o, "tasks").map { t in
            ExtractedTask(title: try str(t, "title"), description: try str(t, "description", required: false),
                          priority: try str(t, "priority", required: false), dueDate: try str(t, "due_date", required: false),
                          evidence: try str(t, "evidence", required: false))
        }
        return ExtractOutput(tasks: tasks)
    }

    public static func parsePlan(_ text: String) throws -> PlanOutput {
        let o = try extractJSON(text)
        let tasks = try objList(o, "tasks").map { t in
            PlannedTask(title: try str(t, "title"), description: try str(t, "description", required: false),
                        priority: try str(t, "priority", required: false), source: try str(t, "source", required: false),
                        rationale: try str(t, "rationale", required: false))
        }
        return PlanOutput(summary: try str(o, "summary", required: false), tasks: tasks)
    }

    public static func parseDraft(_ text: String) throws -> DraftOutput {
        let o = try extractJSON(text)
        let title = try str(o, "title")
        let markdown = try str(o, "markdown")
        if title.isEmpty || markdown.isEmpty { throw SlothBossError.modelOutput("The draft needs a title and a body.") }
        return DraftOutput(title: title, markdown: markdown, citations: try strList(o, "citations"))
    }

    // MARK: citations

    private static let markerRx = RegexPattern(#"\[\s*(S\d+(?:\s*[,;]\s*S\d+)*)\s*\]"#, caseInsensitive: true)
    private static let spaceBeforePunctRx = RegexPattern(#"[ \t]+([.,;:!?])"#)
    private static let multiSpaceRx = RegexPattern(#"[ \t]{2,}"#)

    /// Keeps only citation markers that refer to sources actually retrieved for this request.
    /// Invented ids are removed from the text and counted, so the UI can say so.
    public static func validateCitations(_ text: String, declared: [String], evidence: Evidence) -> CitationCheck {
        var valid: [String] = []
        var invalid = Set<String>()
        func canon(_ id: String) -> String { id.trimmed.uppercased() }

        var cleaned = markerRx.replace(text) { groups in
            let ids = groups[1].components(separatedBy: CharacterSet(charactersIn: ",;")).map(canon)
            var keep: [String] = []
            for id in ids {
                if evidence.find(id) != nil {
                    keep.append(id)
                    if !valid.contains(id) { valid.append(id) }
                } else {
                    invalid.insert(id)
                }
            }
            return keep.isEmpty ? "" : "[" + keep.joined(separator: ", ") + "]"
        }

        for raw in declared {
            let id = canon(raw.trimming(["[", "]"]))
            if evidence.find(id) != nil {
                if !valid.contains(id) { valid.append(id) }
            } else {
                invalid.insert(id)
            }
        }

        cleaned = multiSpaceRx.replace(spaceBeforePunctRx.replace(cleaned, template: "$1"), template: " ").trimmed
        return CitationCheck(text: cleaned, validIds: valid, invalidCount: invalid.count)
    }
}
