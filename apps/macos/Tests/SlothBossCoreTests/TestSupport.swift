import Foundation
import XCTest
@testable import SlothBossCore

enum Fixtures {
    /// Repository root, found by walking up from this source file to the folder that contains contracts/.
    static let root: URL = {
        var dir = URL(fileURLWithPath: #filePath).deletingLastPathComponent()
        let fm = FileManager.default
        while dir.path != "/" {
            var isDir: ObjCBool = false
            if fm.fileExists(atPath: dir.appendingPathComponent("contracts/schema").path, isDirectory: &isDir), isDir.boolValue {
                return dir
            }
            dir = dir.deletingLastPathComponent()
        }
        fatalError("contracts/ folder not found above \(#filePath)")
    }()

    static var contracts: URL { root.appendingPathComponent("contracts") }

    static func read(_ relative: String) throws -> String {
        let data = try Data(contentsOf: contracts.appendingPathComponent("fixtures").appendingPathComponent(relative))
        return String(decoding: data, as: UTF8.self)
    }

    static func json(_ relative: String) throws -> JSON { try JSON.parse(read(relative)) }
}

extension String {
    /// .NET `TrimEnd()` (whitespace only).
    var trimmingTrailingWhitespace: String {
        var s = Substring(self)
        while let l = s.last, l.isWhitespace { s = s.dropLast() }
        return String(s)
    }
}

/// A temporary on-disk workspace, deleted by `dispose()`.
final class TempWorkspace {
    let dir: URL
    var dbPath: String { dir.appendingPathComponent("workspace.db").path }
    private(set) var ws: Workspace

    init() throws {
        dir = FileManager.default.temporaryDirectory
            .appendingPathComponent("slothboss-tests", isDirectory: true)
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        ws = try Workspace.open(path: dir.appendingPathComponent("workspace.db").path)
    }

    func reopen() throws -> Workspace {
        ws.close()
        ws = try Workspace.open(path: dbPath)
        return ws
    }

    func dispose() {
        ws.close()
        try? FileManager.default.removeItem(at: dir)
    }
}

extension XCTestCase {
    func makeWorkspace() throws -> TempWorkspace {
        let t = try TempWorkspace()
        addTeardownBlock { t.dispose() }
        return t
    }

    /// Advances the clock 1 s per read so updated_at comparisons are deterministic. Restored at teardown.
    func useSteppingClock() {
        let previous = SBClock.now
        let stepper = SteppingClock()
        SBClock.now = { stepper.next() }
        addTeardownBlock { SBClock.now = previous }
    }

    func assertThrowsKind(_ kind: SlothBossError.Kind? = nil, _ body: () throws -> Void, file: StaticString = #filePath, line: UInt = #line) {
        do {
            try body()
            XCTFail("Expected an error", file: file, line: line)
        } catch let e as SlothBossError {
            if let kind { XCTAssertEqual(e.kind, kind, e.message, file: file, line: line) }
        } catch {
            XCTFail("Unexpected error type: \(error)", file: file, line: line)
        }
    }
}

final class SteppingClock: @unchecked Sendable {
    private let lock = NSLock()
    private var t = ISO8601DateFormatter().date(from: "2026-10-20T09:00:00Z")!

    func next() -> Date {
        lock.lock()
        defer { lock.unlock() }
        t = t.addingTimeInterval(1)
        return t
    }
}

// MARK: - Scripted Ollama (URLProtocol stub)

/// Scripted stand-in for the Ollama HTTP API. Replies to /api/chat are dequeued in order and
/// streamed as NDJSON exactly like Ollama does. Records every chat request for assertions.
final class ScriptedOllama: @unchecked Sendable {
    private let lock = NSLock()
    private var replies: [String] = []
    private var requests: [JSON] = []
    private var caps: [String] = ["completion"]

    var capabilities: [String] {
        get { lock.lock(); defer { lock.unlock() }; return caps }
        set { lock.lock(); caps = newValue; lock.unlock() }
    }

    var chatRequests: [JSON] { lock.lock(); defer { lock.unlock() }; return requests }

    func enqueue(_ reply: String) { lock.lock(); replies.append(reply); lock.unlock() }

    struct Response {
        let status: Int
        let contentType: String
        let chunks: [Data]
    }

    private static func json(_ s: String, status: Int = 200) -> Response {
        Response(status: status, contentType: "application/json", chunks: [Data(s.utf8)])
    }

    func respond(path: String, body: Data?) -> Response {
        switch path {
        case "/api/version":
            return ScriptedOllama.json("{\"version\":\"0.0-test\"}")
        case "/api/tags":
            return ScriptedOllama.json("""
                {"models":[{"name":"gemma4:e2b-it-qat","size":4336358185,"details":{"family":"gemma4","families":["gemma4"],"parameter_size":"4.6B","quantization_level":"Q4_0"}},{"name":"llama3:8b","size":1,"details":{"family":"llama","families":["llama"]}}]}
                """)
        case "/api/show":
            let list = JSON.array(capabilities.map { .string($0) })
            return ScriptedOllama.json(JSON.object(JSONObject([("capabilities", list)])).serialized())
        case "/api/chat":
            lock.lock()
            if let body, let parsed = try? JSON.parse(body) { requests.append(parsed) }
            let reply: String? = replies.isEmpty ? nil : replies.removeFirst()
            lock.unlock()
            guard let reply else { return ScriptedOllama.json("{\"error\":\"no scripted reply\"}", status: 500) }
            if reply.hasPrefix("HTTP404:") { return ScriptedOllama.json("{\"error\":\"model 'x' not found\"}", status: 404) }
            // Split into small chunks (and split the NDJSON stream itself at odd byte offsets) to
            // exercise streaming reassembly.
            var stream = ""
            let chars = Array(reply)
            var i = 0
            while i < chars.count {
                let piece = String(chars[i..<min(i + 7, chars.count)])
                let message = JSON.object(JSONObject([("role", "assistant"), ("content", .string(piece))]))
                stream += JSON.object(JSONObject([("message", message), ("done", false)])).serialized() + "\n"
                i += 7
            }
            stream += "{\"model\":\"gemma4:e2b-it-qat\",\"done\":true,\"done_reason\":\"stop\"}\n"
            let bytes = Array(stream.utf8)
            var chunks: [Data] = []
            var j = 0
            while j < bytes.count {
                chunks.append(Data(bytes[j..<min(j + 23, bytes.count)]))
                j += 23
            }
            return Response(status: 200, contentType: "application/x-ndjson", chunks: chunks)
        default:
            return Response(status: 404, contentType: "text/plain", chunks: [])
        }
    }
}

final class StubURLProtocol: URLProtocol {
    private static let lock = NSLock()
    private static var _current: ScriptedOllama?

    static var current: ScriptedOllama? {
        get { lock.lock(); defer { lock.unlock() }; return _current }
        set { lock.lock(); _current = newValue; lock.unlock() }
    }

    override class func canInit(with request: URLRequest) -> Bool { true }
    override class func canonicalRequest(for request: URLRequest) -> URLRequest { request }

    override func startLoading() {
        guard let url = request.url, let script = StubURLProtocol.current else {
            client?.urlProtocol(self, didFailWithError: URLError(.cannotConnectToHost))
            return
        }
        let body = request.httpBody ?? StubURLProtocol.readAll(request.httpBodyStream)
        let r = script.respond(path: url.path, body: body)
        let response = HTTPURLResponse(url: url, statusCode: r.status, httpVersion: "HTTP/1.1",
                                       headerFields: ["Content-Type": r.contentType])!
        client?.urlProtocol(self, didReceive: response, cacheStoragePolicy: .notAllowed)
        for chunk in r.chunks { client?.urlProtocol(self, didLoad: chunk) }
        client?.urlProtocolDidFinishLoading(self)
    }

    override func stopLoading() {}

    /// URLSession hands bodies to protocols as streams, so read it back.
    static func readAll(_ stream: InputStream?) -> Data? {
        guard let stream else { return nil }
        stream.open()
        defer { stream.close() }
        var data = Data()
        var buffer = [UInt8](repeating: 0, count: 4096)
        while stream.hasBytesAvailable {
            let n = stream.read(&buffer, maxLength: buffer.count)
            if n <= 0 { break }
            data.append(buffer, count: n)
        }
        return data
    }

    static func makeSession(_ script: ScriptedOllama) -> URLSession {
        current = script
        let config = URLSessionConfiguration.ephemeral
        config.protocolClasses = [StubURLProtocol.self]
        return URLSession(configuration: config)
    }
}
