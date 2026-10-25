using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlothBoss.Core.Ai;

public class OllamaException(string message, Exception? inner = null) : SlothBossException(message, inner);
public sealed class OllamaUnavailableException(string message, Exception? inner = null) : OllamaException(message, inner);
public sealed class ModelNotFoundException(string message) : OllamaException(message);
public sealed class InferenceTimeoutException(string message) : OllamaException(message);

public sealed record OllamaModel(
    string Name, string Family, IReadOnlyList<string> Families, string ParameterSize, string Quantization, long SizeBytes)
{
    public bool IsGemma =>
        Family.StartsWith("gemma", StringComparison.OrdinalIgnoreCase)
        || Families.Any(f => f.StartsWith("gemma", StringComparison.OrdinalIgnoreCase))
        || Name.StartsWith("gemma", StringComparison.OrdinalIgnoreCase);

    public string SizeLabel => SizeBytes <= 0 ? "" : $"{SizeBytes / 1_000_000_000.0:0.0} GB";
}

/// <summary>A chat message. <paramref name="Images"/> are base64-encoded images for vision-capable models.</summary>
public sealed record ChatMessage(string Role, string Content, IReadOnlyList<string>? Images = null);

public sealed record ChatRequest(
    string Model, IReadOnlyList<ChatMessage> Messages, JsonNode? Format = null, double Temperature = 0.2, int ContextTokens = 8192);

public sealed record ChatResult(string Content, string Model, TimeSpan Elapsed, string? DoneReason);

/// <summary>
/// Minimal client for Ollama's documented local REST API (/api/version, /api/tags, /api/show, /api/chat).
/// The endpoint must be a loopback address; any other host is rejected so workspace content can
/// never be sent off the machine through this client.
/// </summary>
public sealed class OllamaClient : IDisposable
{
    public static readonly Uri DefaultEndpoint = new("http://127.0.0.1:11434/");

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Dictionary<string, IReadOnlyList<string>> _capabilities = new(StringComparer.Ordinal);

    public Uri Endpoint { get; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(180);

    public OllamaClient(Uri? endpoint = null, HttpMessageHandler? handler = null)
    {
        Endpoint = ValidateEndpoint(endpoint ?? DefaultEndpoint);
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _ownsHttp = true;
        _http.Timeout = System.Threading.Timeout.InfiniteTimeSpan; // per-request timeouts below
    }

    public static Uri ValidateEndpoint(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("http" or "https"))
            throw new OllamaException("The Ollama endpoint must be an http:// URL.");
        var host = uri.Host.Trim('[', ']');
        var isLoopback = host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                         || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
        if (!isLoopback)
            throw new OllamaException($"SlothBoss only talks to a local Ollama server (localhost). '{uri.Host}' is not a loopback address.");
        return uri.AbsolutePath.EndsWith('/') ? uri : new Uri(uri + "/");
    }

    public static Uri ParseEndpoint(string text) =>
        Uri.TryCreate(text.Trim(), UriKind.Absolute, out var u) ? ValidateEndpoint(u) : throw new OllamaException($"'{text}' is not a valid URL.");

    private CancellationTokenSource Linked(CancellationToken ct, TimeSpan timeout)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, HttpCompletionOption option, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = Linked(ct, timeout);
        try
        {
            var resp = await _http.SendAsync(req, option, cts.Token).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                resp.Dispose();
                var message = TryError(body) ?? $"HTTP {(int)resp.StatusCode}";
                if (resp.StatusCode == HttpStatusCode.NotFound && message.Contains("not found", StringComparison.OrdinalIgnoreCase))
                    throw new ModelNotFoundException($"Ollama reports: {message}. Pull the model first (see Settings → Local AI).");
                throw new OllamaException($"Ollama returned an error: {message}");
            }
            return resp;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InferenceTimeoutException($"The local model did not respond within {timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException e) when (e.InnerException is SocketException || e.HttpRequestError == HttpRequestError.ConnectionError)
        {
            throw new OllamaUnavailableException($"Ollama is not reachable at {Endpoint}. Is it installed and running?", e);
        }
    }

    private static string? TryError(string body)
    {
        try { return JsonNode.Parse(body)?["error"]?.GetValue<string>(); }
        catch (Exception) { return body.Length is > 0 and < 300 ? body : null; }
    }

    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(Endpoint, "api/version"));
        using var resp = await SendAsync(req, HttpCompletionOption.ResponseContentRead, TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return json?["version"]?.GetValue<string>() ?? "unknown";
    }

    public async Task<IReadOnlyList<OllamaModel>> ListModelsAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(Endpoint, "api/tags"));
        using var resp = await SendAsync(req, HttpCompletionOption.ResponseContentRead, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var list = new List<OllamaModel>();
        foreach (var m in json?["models"]?.AsArray() ?? [])
        {
            if (m is null) continue;
            var details = m["details"];
            list.Add(new OllamaModel(
                m["name"]?.GetValue<string>() ?? m["model"]?.GetValue<string>() ?? "",
                details?["family"]?.GetValue<string>() ?? "",
                details?["families"]?.AsArray().Select(f => f?.GetValue<string>() ?? "").ToList() ?? [],
                details?["parameter_size"]?.GetValue<string>() ?? "",
                details?["quantization_level"]?.GetValue<string>() ?? "",
                m["size"]?.GetValue<long>() ?? 0));
        }
        return list.Where(m => m.Name.Length > 0).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Model capabilities from /api/show (e.g. "completion", "tools", "thinking"). Cached per model.</summary>
    public async Task<IReadOnlyList<string>> GetCapabilitiesAsync(string model, CancellationToken ct = default)
    {
        lock (_capabilities)
            if (_capabilities.TryGetValue(model, out var cached)) return cached;
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(Endpoint, "api/show"))
        {
            Content = JsonContent(new JsonObject { ["model"] = model }),
        };
        using var resp = await SendAsync(req, HttpCompletionOption.ResponseContentRead, TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        IReadOnlyList<string> caps = json?["capabilities"]?.AsArray().Select(c => c?.GetValue<string>() ?? "").ToList() ?? ["completion"];
        lock (_capabilities) _capabilities[model] = caps;
        return caps;
    }

    /// <summary>
    /// Streams a chat completion. <paramref name="onDelta"/> receives text as it is generated.
    /// Cancelling <paramref name="ct"/> aborts the request; exceeding <see cref="Timeout"/> throws <see cref="InferenceTimeoutException"/>.
    /// </summary>
    public async Task<ChatResult> ChatAsync(ChatRequest request, Action<string>? onDelta = null, CancellationToken ct = default)
    {
        var caps = await GetCapabilitiesAsync(request.Model, ct).ConfigureAwait(false);
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["stream"] = true,
            ["messages"] = new JsonArray(request.Messages.Select(m =>
            {
                var msg = new JsonObject { ["role"] = m.Role, ["content"] = m.Content };
                if (m.Images is { Count: > 0 }) msg["images"] = new JsonArray(m.Images.Select(i => (JsonNode)i).ToArray());
                return (JsonNode)msg;
            }).ToArray()),
            ["options"] = new JsonObject { ["temperature"] = request.Temperature, ["num_ctx"] = request.ContextTokens },
            ["keep_alive"] = "10m",
        };
        // Thinking output is not shown and costs time on small machines; disable it where supported.
        if (caps.Contains("thinking")) body["think"] = false;
        if (request.Format is not null) body["format"] = request.Format.DeepClone();

        var started = DateTime.UtcNow;
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(Endpoint, "api/chat")) { Content = JsonContent(body) };
        using var cts = Linked(ct, Timeout);
        try
        {
            using var resp = await SendAsync(req, HttpCompletionOption.ResponseHeadersRead, Timeout, cts.Token).ConfigureAwait(false);
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var content = new StringBuilder();
            string? doneReason = null;
            string model = request.Model;
            while (await reader.ReadLineAsync(cts.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) continue;
                JsonNode? chunk;
                try { chunk = JsonNode.Parse(line); }
                catch (JsonException e) { throw new OllamaException("Ollama sent a malformed streaming response.", e); }
                if (chunk?["error"]?.GetValue<string>() is { } err) throw new OllamaException($"Inference failed: {err}");
                var delta = chunk?["message"]?["content"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(delta))
                {
                    content.Append(delta);
                    onDelta?.Invoke(delta);
                }
                if (chunk?["done"]?.GetValue<bool>() == true)
                {
                    doneReason = chunk["done_reason"]?.GetValue<string>();
                    model = chunk["model"]?.GetValue<string>() ?? model;
                    break;
                }
            }
            return new ChatResult(content.ToString(), model, DateTime.UtcNow - started, doneReason);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InferenceTimeoutException($"The local model did not finish within {Timeout.TotalSeconds:0} seconds. Try a shorter request or a longer timeout in Settings.");
        }
        catch (IOException e) when (!ct.IsCancellationRequested)
        {
            throw new OllamaUnavailableException("The connection to Ollama was interrupted.", e);
        }
    }

    private static StringContent JsonContent(JsonNode node)
    {
        var content = new StringContent(node.ToJsonString(), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return content;
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
