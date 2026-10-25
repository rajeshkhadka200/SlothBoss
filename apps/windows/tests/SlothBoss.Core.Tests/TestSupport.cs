using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using SlothBoss.Core;
using SlothBoss.Core.Storage;

namespace SlothBoss.Core.Tests;

public static class Fixtures
{
    /// <summary>Repository root, found by walking up to the folder that contains contracts/.</summary>
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "contracts"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("contracts/ folder not found");
        }
    }

    public static string Read(string relative) => File.ReadAllText(Path.Combine(Root, "contracts", "fixtures", relative));
}

/// <summary>A temporary on-disk workspace, deleted on dispose.</summary>
public sealed class TempWorkspace : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "slothboss-tests", Guid.NewGuid().ToString("N"));
    public string DbPath => Path.Combine(Dir, "workspace.db");
    public Workspace Ws { get; }

    public TempWorkspace()
    {
        Directory.CreateDirectory(Dir);
        Ws = Workspace.Open(DbPath);
    }

    public void Dispose()
    {
        Ws.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(Dir, recursive: true); } catch (IOException) { }
    }
}

/// <summary>Advances the clock 1 s per read so updated_at comparisons are deterministic.</summary>
public sealed class SteppingClock : IDisposable
{
    private readonly Func<DateTimeOffset> _previous = Clock.Now;
    private DateTimeOffset _t = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    public SteppingClock() => Clock.Now = () => _t = _t.AddSeconds(1);
    public void Dispose() => Clock.Now = _previous;
}

/// <summary>
/// Scripted stand-in for the Ollama HTTP API. Replies to /api/chat are dequeued in order and
/// streamed as NDJSON exactly like Ollama does. Records every request for assertions.
/// </summary>
public sealed class ScriptedOllama : HttpMessageHandler
{
    public Queue<string> ChatReplies { get; } = new();
    public List<JsonObject> ChatRequests { get; } = [];
    public string[] Capabilities { get; set; } = ["completion"];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var path = request.RequestUri!.AbsolutePath;
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        switch (path)
        {
            case "/api/version":
                return Json("{\"version\":\"0.0-test\"}");
            case "/api/tags":
                return Json("""{"models":[{"name":"gemma4:e2b-it-qat","size":4336358185,"details":{"family":"gemma4","families":["gemma4"],"parameter_size":"4.6B","quantization_level":"Q4_0"}},{"name":"llama3:8b","size":1,"details":{"family":"llama","families":["llama"]}}]}""");
            case "/api/show":
                return Json(new JsonObject { ["capabilities"] = new JsonArray(Capabilities.Select(c => (JsonNode)c).ToArray()) }.ToJsonString());
            case "/api/chat":
                ChatRequests.Add(JsonNode.Parse(body!)!.AsObject());
                if (ChatReplies.Count == 0) return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{\"error\":\"no scripted reply\"}") };
                var reply = ChatReplies.Dequeue();
                if (reply.StartsWith("HTTP404:", StringComparison.Ordinal))
                    return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"model 'x' not found\"}") };
                var sb = new StringBuilder();
                // Split into a few chunks to exercise streaming reassembly.
                for (var i = 0; i < reply.Length; i += 7)
                    sb.Append(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = reply.Substring(i, Math.Min(7, reply.Length - i)) }, ["done"] = false }.ToJsonString()).Append('\n');
                sb.Append("{\"model\":\"gemma4:e2b-it-qat\",\"done\":true,\"done_reason\":\"stop\"}\n");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/x-ndjson") };
            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static HttpResponseMessage Json(string s) =>
        new(HttpStatusCode.OK) { Content = new StringContent(s, Encoding.UTF8, "application/json") };
}
