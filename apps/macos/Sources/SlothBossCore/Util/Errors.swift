import Foundation

/// A user-presentable failure (validation, missing record, unsafe operation, AI problems).
///
/// The C# reference uses an exception hierarchy (SlothBossException and subclasses); here a
/// single error type carries a `kind` so callers and tests can still tell the cases apart.
public struct SlothBossError: Error, LocalizedError, CustomStringConvertible, Sendable, Equatable {
    public enum Kind: String, Sendable, Equatable {
        case general
        case documentFormat
        case actionValidation
        case modelOutput
        case backupValidation
        /// Any Ollama failure that is not one of the more specific cases below.
        case ollama
        case ollamaUnavailable
        case modelNotFound
        case inferenceTimeout
    }

    public let kind: Kind
    public let message: String
    /// Individual problems (backup validation lists every error it found).
    public let details: [String]

    public init(_ message: String, kind: Kind = .general, details: [String] = []) {
        self.kind = kind
        self.message = message
        self.details = details
    }

    /// True for every Ollama-related kind (mirrors `catch (OllamaException)` in the reference).
    public var isOllamaError: Bool {
        switch kind {
        case .ollama, .ollamaUnavailable, .modelNotFound, .inferenceTimeout: return true
        default: return false
        }
    }

    public var errorDescription: String? { message }
    public var description: String { message }

    static func documentFormat(_ message: String) -> SlothBossError { SlothBossError(message, kind: .documentFormat) }
    static func actionValidation(_ message: String) -> SlothBossError { SlothBossError(message, kind: .actionValidation) }
    static func modelOutput(_ message: String) -> SlothBossError { SlothBossError(message, kind: .modelOutput) }
    static func ollama(_ message: String) -> SlothBossError { SlothBossError(message, kind: .ollama) }

    static func backupValidation(_ errors: [String]) -> SlothBossError {
        var text = "The backup is not valid:\n• " + errors.prefix(10).joined(separator: "\n• ")
        if errors.count > 10 { text += "\n• …and \(errors.count - 10) more" }
        return SlothBossError(text, kind: .backupValidation, details: errors)
    }
}

/// Returns a readable message for any error (used by the UI).
public func describeError(_ error: Error) -> String {
    if let e = error as? SlothBossError { return e.message }
    if let e = error as? SQLiteError { return e.message }
    if error is CancellationError { return "Cancelled." }
    return error.localizedDescription
}
