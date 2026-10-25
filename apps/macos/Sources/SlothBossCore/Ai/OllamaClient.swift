import Foundation
#if canImport(Darwin)
import Darwin
#endif

public struct OllamaModel: Equatable, Hashable, Identifiable, Sendable {
    public let name: String
    public let family: String
    public let families: [String]
    public let parameterSize: String
    public let quantization: String
    public let sizeBytes: Int64

    public var id: String { name }

    public var isGemma: Bool {
        family.lowercased().hasPrefix("gemma")
            || families.contains { $0.lowercased().hasPrefix("gemma") }
            || name.lowercased().hasPrefix("gemma")
    }

    public var sizeLabel: String {
        sizeBytes <= 0 ? "" : String(format: "%.1f GB", Double(sizeBytes) / 1_000_000_000.0)
    }
}

public struct ChatMessage: Equatable, Sendable {
    public let role: String
    public let content: String
    public init(_ role: String, _ content: String) {
        self.role = role
        self.content = content
    }
}

public struct ChatRequest: Sendable {
    public var model: String
    public var messages: [ChatMessage]
    public var format: JSON?
    public var temperature: Double
    public var contextTokens: Int

    public init(model: String, messages: [ChatMessage], format: JSON? = nil, temperature: Double = 0.2, contextTokens: Int = 8192) {
        self.model = model
        self.messages = messages
        self.format = format
        self.temperature = temperature
        self.contextTokens = contextTokens
    }
}

public struct ChatResult: Sendable {
    public let content: String
    public let model: String
    public let elapsed: TimeInterval
    public let doneReason: String?
}

/// Minimal client for Ollama's documented local REST API (/api/version, /api/tags, /api/show, /api/chat).
/// The endpoint must be a loopback address; any other host is rejected so workspace content can
/// never be sent off the machine through this client.
public final class OllamaClient: @unchecked Sendable {
    public static let defaultEndpoint = URL(string: "http://127.0.0.1:11434/")!

    public let endpoint: URL
    private let session: URLSession
    private let lock = NSLock()
    private var capabilityCache: [String: [String]] = [:]
    private var timeoutSeconds: TimeInterval = 180

    /// Total time allowed for one chat completion.
    public var timeout: TimeInterval {
        get { lock.lock(); defer { lock.unlock() }; return timeoutSeconds }
        set { lock.lock(); timeoutSeconds = newValue; lock.unlock() }
    }

    /// - Parameter session: injectable for tests (a session whose configuration has a stub URLProtocol).
    public init(endpoint: URL? = nil, session: URLSession? = nil) throws {
        self.endpoint = try OllamaClient.validateEndpoint(endpoint ?? OllamaClient.defaultEndpoint)
        if let session {
            self.session = session
        } else {
            let config = URLSessionConfiguration.ephemeral
            config.waitsForConnectivity = false
            config.timeoutIntervalForRequest = 600
            config.timeoutIntervalForResource = 3600
            config.urlCache = nil
            // Never route loopback traffic through a proxy (keys are kCFNetworkProxies* values).
            config.connectionProxyDictionary = [
                "HTTPEnable": 0, "HTTPSEnable": 0, "ProxyAutoConfigEnable": 0, "SOCKSEnable": 0,
            ]
            self.session = URLSession(configuration: config)
        }
    }

    // MARK: endpoint validation

    public static func validateEndpoint(_ url: URL) throws -> URL {
        guard let scheme = url.scheme?.lowercased(), scheme == "http" || scheme == "https" else {
            throw SlothBossError.ollama("The Ollama endpoint must be an http:// URL.")
        }
        guard let rawHost = URLComponents(url: url, resolvingAgainstBaseURL: false)?.host ?? url.host, !rawHost.isEmpty else {
            throw SlothBossError.ollama("The Ollama endpoint must include a host such as 127.0.0.1.")
        }
        let host = rawHost.trimmingCharacters(in: CharacterSet(charactersIn: "[]"))
        guard isLoopbackHost(host) else {
            throw SlothBossError.ollama("SlothBoss only talks to a local Ollama server (localhost). '\(rawHost)' is not a loopback address.")
        }
        if url.user != nil || url.password != nil {
            throw SlothBossError.ollama("The Ollama endpoint must not contain credentials.")
        }
        let text = url.absoluteString
        if text.hasSuffix("/") { return url }
        guard let withSlash = URL(string: text + "/") else { throw SlothBossError.ollama("'\(text)' is not a valid URL.") }
        return withSlash
    }

    public static func parseEndpoint(_ text: String) throws -> URL {
        let trimmed = text.trimmed
        guard !trimmed.isEmpty, !trimmed.contains(" "), let url = URL(string: trimmed), url.scheme != nil else {
            throw SlothBossError.ollama("'\(text)' is not a valid URL.")
        }
        return try validateEndpoint(url)
    }

    /// "localhost", any 127.0.0.0/8 IPv4 address, or ::1.
    static func isLoopbackHost(_ host: String) -> Bool {
        if host.lowercased() == "localhost" { return true }
        var v4 = in_addr()
        if inet_pton(AF_INET, host, &v4) == 1 {
            // s_addr is in network byte order; the first octet is the lowest-addressed byte.
            let firstOctet = withUnsafeBytes(of: &v4) { $0[0] }
            return firstOctet == 127
        }
        var v6 = in6_addr()
        if inet_pton(AF_INET6, host, &v6) == 1 {
            let bytes = withUnsafeBytes(of: &v6) { Array($0) }
            return bytes.count == 16 && bytes[0..<15].allSatisfy { $0 == 0 } && bytes[15] == 1
        }
        return false
    }

    private func url(_ path: String) -> URL {
        endpoint.appendingPathComponent(path)
    }

    // MARK: transport

    /// Runs `operation`, failing with an inference-timeout error after `seconds`. User cancellation
    /// (cancelling the surrounding Task) is reported as CancellationError, never as a timeout.
    private func withTimeout<T: Sendable>(_ seconds: TimeInterval, message: String,
                                          _ operation: @escaping @Sendable () async throws -> T) async throws -> T {
        try await withThrowingTaskGroup(of: T.self) { group in
            group.addTask { try await operation() }
            group.addTask {
                try await Task.sleep(nanoseconds: UInt64(max(0.001, seconds) * 1_000_000_000))
                throw SlothBossError(message, kind: .inferenceTimeout)
            }
            defer { group.cancelAll() }
            guard let value = try await group.next() else { throw CancellationError() }
            return value
        }
    }

    /// Maps transport errors to SlothBoss errors; keeps cancellation as CancellationError.
    private func mapError(_ error: Error) -> Error {
        if Task.isCancelled { return CancellationError() }
        if error is CancellationError { return error }
        if let e = error as? SlothBossError { return e }
        if let e = error as? URLError {
            switch e.code {
            case .cancelled:
                return CancellationError()
            case .cannotConnectToHost, .cannotFindHost, .networkConnectionLost, .notConnectedToInternet,
                 .dnsLookupFailed, .timedOut, .secureConnectionFailed, .badServerResponse, .resourceUnavailable:
                if e.code == .networkConnectionLost {
                    return SlothBossError("The connection to Ollama was interrupted.", kind: .ollamaUnavailable)
                }
                return SlothBossError("Ollama is not reachable at \(endpoint.absoluteString). Is it installed and running?", kind: .ollamaUnavailable)
            default:
                return SlothBossError("Could not talk to Ollama: \(e.localizedDescription)", kind: .ollama)
            }
        }
        return SlothBossError("Could not talk to Ollama: \(error.localizedDescription)", kind: .ollama)
    }

    private static func errorMessage(from data: Data) -> String? {
        if let json = try? JSON.parse(data), let message = json["error"]?.stringValue { return message }
        let text = String(decoding: data, as: UTF8.self)
        return (1..<300).contains(text.count) ? text : nil
    }

    private func checkStatus(_ response: URLResponse, body: Data) throws {
        guard let http = response as? HTTPURLResponse else { throw SlothBossError.ollama("Ollama sent an invalid response.") }
        guard !(200..<300).contains(http.statusCode) else { return }
        let message = OllamaClient.errorMessage(from: body) ?? "HTTP \(http.statusCode)"
        if http.statusCode == 404 && message.lowercased().contains("not found") {
            throw SlothBossError("Ollama reports: \(message). Pull the model first (see Settings → Local AI).", kind: .modelNotFound)
        }
        throw SlothBossError.ollama("Ollama returned an error: \(message)")
    }

    private func jsonRequest(_ path: String, method: String, body: JSON?) -> URLRequest {
        var request = URLRequest(url: url(path))
        request.httpMethod = method
        request.cachePolicy = .reloadIgnoringLocalCacheData
        if let body {
            request.setValue("application/json", forHTTPHeaderField: "Content-Type")
            request.httpBody = Data(body.serialized().utf8)
        }
        return request
    }

    /// Simple request/response call with a total timeout.
    private func fetchJSON(_ path: String, method: String = "GET", body: JSON? = nil, timeout seconds: TimeInterval) async throws -> JSON {
        try Task.checkCancellation()
        let request = jsonRequest(path, method: method, body: body)
        let session = self.session
        do {
            let data: Data = try await withTimeout(seconds, message: "Ollama did not respond within \(Int(seconds)) seconds.") {
                let (data, response) = try await session.data(for: request)
                try self.checkStatus(response, body: data)
                return data
            }
            do { return try JSON.parse(data) } catch { throw SlothBossError.ollama("Ollama sent a response that is not valid JSON.") }
        } catch {
            throw mapError(error)
        }
    }

    // MARK: API

    public func version() async throws -> String {
        let json = try await fetchJSON("api/version", timeout: 5)
        return json["version"]?.stringValue ?? "unknown"
    }

    public func listModels() async throws -> [OllamaModel] {
        let json = try await fetchJSON("api/tags", timeout: 10)
        var list: [OllamaModel] = []
        for m in json["models"]?.arrayValue ?? [] {
            let details = m["details"]
            let name = m["name"]?.stringValue ?? m["model"]?.stringValue ?? ""
            list.append(OllamaModel(
                name: name,
                family: details?["family"]?.stringValue ?? "",
                families: details?["families"]?.arrayValue?.map { $0.stringValue ?? "" } ?? [],
                parameterSize: details?["parameter_size"]?.stringValue ?? "",
                quantization: details?["quantization_level"]?.stringValue ?? "",
                sizeBytes: m["size"]?.intValue ?? 0))
        }
        return list.filter { !$0.name.isEmpty }.sorted { $0.name.lowercased() < $1.name.lowercased() }
    }

    /// Model capabilities from /api/show (e.g. "completion", "tools", "thinking"). Cached per model.
    public func capabilities(of model: String) async throws -> [String] {
        lock.lock()
        let cached = capabilityCache[model]
        lock.unlock()
        if let cached { return cached }
        let json = try await fetchJSON("api/show", method: "POST", body: .object(JSONObject([("model", .string(model))])), timeout: 15)
        let caps = json["capabilities"]?.arrayValue?.map { $0.stringValue ?? "" } ?? ["completion"]
        lock.lock()
        capabilityCache[model] = caps
        lock.unlock()
        return caps
    }

    /// Streams a chat completion. `onDelta` receives text as it is generated (on a background thread).
    /// Cancelling the calling Task aborts the request (CancellationError); exceeding `timeout`
    /// throws an `.inferenceTimeout` error.
    public func chat(_ request: ChatRequest, onDelta: (@Sendable (String) -> Void)? = nil) async throws -> ChatResult {
        try Task.checkCancellation()
        let caps = try await capabilities(of: request.model)
        var body = JSONObject()
        body["model"] = .string(request.model)
        body["stream"] = .bool(true)
        body["messages"] = .array(request.messages.map { .object(JSONObject([("role", .string($0.role)), ("content", .string($0.content))])) })
        body["options"] = .object(JSONObject([("temperature", .double(request.temperature)), ("num_ctx", .int(Int64(request.contextTokens)))]))
        body["keep_alive"] = .string("10m")
        // Thinking output is not shown and costs time on small machines; disable it where supported.
        if caps.contains("thinking") { body["think"] = .bool(false) }
        if let format = request.format { body["format"] = format }

        let urlRequest = jsonRequest("api/chat", method: "POST", body: .object(body))
        let seconds = timeout
        let session = self.session
        let started = Date()
        let model = request.model
        do {
            return try await withTimeout(seconds, message:
                "The local model did not finish within \(Int(seconds)) seconds. Try a shorter request or a longer timeout in Settings.") {
                let (bytes, response) = try await session.bytes(for: urlRequest)
                if let http = response as? HTTPURLResponse, !(200..<300).contains(http.statusCode) {
                    var data = Data()
                    for try await byte in bytes {
                        data.append(byte)
                        if data.count > 64 * 1024 { break }
                    }
                    try self.checkStatus(response, body: data)
                }
                var content = ""
                var doneReason: String?
                var finalModel = model
                for try await line in bytes.lines {
                    if line.trimmed.isEmpty { continue }
                    let chunk: JSON
                    do { chunk = try JSON.parse(line) } catch {
                        throw SlothBossError.ollama("Ollama sent a malformed streaming response.")
                    }
                    if let err = chunk["error"]?.stringValue { throw SlothBossError.ollama("Inference failed: \(err)") }
                    if let delta = chunk["message"]?["content"]?.stringValue, !delta.isEmpty {
                        content += delta
                        onDelta?(delta)
                    }
                    if chunk["done"]?.boolValue == true {
                        doneReason = chunk["done_reason"]?.stringValue
                        finalModel = chunk["model"]?.stringValue ?? finalModel
                        break
                    }
                }
                return ChatResult(content: content, model: finalModel, elapsed: Date().timeIntervalSince(started), doneReason: doneReason)
            }
        } catch {
            throw mapError(error)
        }
    }
}
