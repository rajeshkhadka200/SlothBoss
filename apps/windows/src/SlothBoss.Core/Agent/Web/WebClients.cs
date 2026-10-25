using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace SlothBoss.Core.Agent.Web;

public class WebServiceException(string message, bool transient = false, Exception? inner = null) : SlothBossException(message, inner)
{
    public bool Transient { get; } = transient;
}

/// <summary>
/// Web research settings. Off by default: the user must enable it and provide keys. Keys are supplied by
/// the host (encrypted store or environment variables) and never written to the workspace database.
/// </summary>
public sealed class WebConfig
{
    public required Func<bool> Enabled { get; init; }
    public required Func<string?> SerpApiKey { get; init; }
    public required Func<string?> BrowserbaseKey { get; init; }
    public Func<string?> BrowserbaseProjectId { get; init; } = () => null;
    public HttpMessageHandler? Handler { get; init; }

    public string? SearchUnavailableReason =>
        !Enabled() ? "Web research is turned off (Settings → Web research)." :
        string.IsNullOrWhiteSpace(SerpApiKey()) ? "No SerpApi key is set (Settings → Web research)." : null;

    public string? ReadUnavailableReason =>
        !Enabled() ? "Web research is turned off (Settings → Web research)." :
        string.IsNullOrWhiteSpace(BrowserbaseKey()) ? "No Browserbase key is set (Settings → Web research)." : null;
}

public sealed record SearchResult(string Title, string Url, string Snippet, string? Source, int Position);
public sealed record SerpAccount(string? PlanName, long? SearchesLeft, long? ThisMonthUsage);
public sealed record FetchedPage(string Url, int StatusCode, string ContentType, string Content);

internal static class Http
{
    public static HttpClient Create(HttpMessageHandler? handler)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(45);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SlothBoss", "0.3"));
        return http;
    }

    public static async Task<(HttpStatusCode Status, JsonNode? Json, string Body)> SendAsync(HttpClient http, HttpRequestMessage req, string service, CancellationToken ct)
    {
        try
        {
            using var resp = await http.SendAsync(req, ct).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonNode? json = null;
            try { json = body.Length > 0 ? JsonNode.Parse(body) : null; } catch (System.Text.Json.JsonException) { }
            return (resp.StatusCode, json, body);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new WebServiceException($"{service} didn't respond in time.", transient: true, e);
        }
        catch (HttpRequestException e) when (e.InnerException is SocketException || e.HttpRequestError is HttpRequestError.ConnectionError or HttpRequestError.NameResolutionError)
        {
            throw new WebServiceException($"Couldn't reach {service}. Check your internet connection.", transient: true, e);
        }
    }
}

/// <summary>SerpApi Google Search (https://serpapi.com/search-api). Each search uses one credit.</summary>
public sealed class SerpApiClient(string apiKey, HttpMessageHandler? handler = null) : IDisposable
{
    public static readonly Uri BaseUri = new("https://serpapi.com/");
    private readonly HttpClient _http = Http.Create(handler);

    private static WebServiceException Error(HttpStatusCode status, JsonNode? json)
    {
        var msg = json?["error"]?.GetValue<string>();
        return status switch
        {
            HttpStatusCode.Unauthorized => new WebServiceException("SerpApi rejected the API key. Check it in Settings → Web research."),
            HttpStatusCode.Forbidden => new WebServiceException("SerpApi refused the request for this account" + (msg is null ? "." : $": {msg}")),
            HttpStatusCode.TooManyRequests => new WebServiceException(msg?.Contains("run out", StringComparison.OrdinalIgnoreCase) == true
                ? "Your SerpApi account has run out of searches." : "SerpApi's hourly limit was reached. Try again later.", transient: msg?.Contains("run out", StringComparison.OrdinalIgnoreCase) != true),
            >= HttpStatusCode.InternalServerError => new WebServiceException("SerpApi had a temporary problem" + (msg is null ? "." : $": {msg}"), transient: true),
            _ => new WebServiceException("SerpApi error" + (msg is null ? $" (HTTP {(int)status})." : $": {msg}")),
        };
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, int max, CancellationToken ct)
    {
        var url = new Uri(BaseUri, $"search.json?engine=google&q={Uri.EscapeDataString(query)}&num={Math.Clamp(max, 1, 10)}&api_key={Uri.EscapeDataString(apiKey)}");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var (status, json, _) = await Http.SendAsync(_http, req, "SerpApi", ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK || json is null) throw Error(status, json);
        if (json["error"]?.GetValue<string>() is { } err)
        {
            // SerpApi reports "no results" as an error string on an otherwise successful search.
            if (err.Contains("hasn't returned any results", StringComparison.OrdinalIgnoreCase)) return [];
            throw new WebServiceException("SerpApi: " + err);
        }
        var results = new List<SearchResult>();
        foreach (var r in json["organic_results"]?.AsArray() ?? [])
        {
            var link = r?["link"]?.GetValue<string>();
            var title = r?["title"]?.GetValue<string>();
            if (link is null || title is null || !Uri.TryCreate(link, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) continue;
            results.Add(new SearchResult(title.Trim(), link, r?["snippet"]?.GetValue<string>()?.Trim() ?? "", r?["source"]?.GetValue<string>(),
                r?["position"]?.GetValue<int>() ?? results.Count + 1));
            if (results.Count >= max) break;
        }
        return results;
    }

    /// <summary>Checks the key without spending a search (SerpApi Account API).</summary>
    public async Task<SerpAccount> AccountAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, $"account.json?api_key={Uri.EscapeDataString(apiKey)}"));
        var (status, json, _) = await Http.SendAsync(_http, req, "SerpApi", ct).ConfigureAwait(false);
        if (status != HttpStatusCode.OK || json is null || json["error"] is not null) throw Error(status == HttpStatusCode.OK ? HttpStatusCode.Unauthorized : status, json);
        return new SerpAccount(json["plan_name"]?.GetValue<string>(), json["total_searches_left"]?.GetValue<long>(), json["this_month_usage"]?.GetValue<long>());
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Browserbase Fetch API (https://docs.browserbase.com/reference/api/fetch-a-page): Browserbase retrieves a
/// page on its infrastructure and returns it as Markdown. Used only for reading; nothing is submitted.
/// </summary>
public sealed class BrowserbaseClient(string apiKey, HttpMessageHandler? handler = null) : IDisposable
{
    public static readonly Uri BaseUri = new("https://api.browserbase.com/");
    private readonly HttpClient _http = Http.Create(handler);

    public async Task<FetchedPage> FetchMarkdownAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var target) || target.Scheme is not ("http" or "https"))
            throw new WebServiceException("Only http(s) pages can be read.");
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "v1/fetch"))
        {
            Content = new StringContent(new JsonObject { ["url"] = url, ["format"] = "markdown", ["allowRedirects"] = true }.ToJsonString(),
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("X-BB-API-Key", apiKey);
        var (status, json, _) = await Http.SendAsync(_http, req, "Browserbase", ct).ConfigureAwait(false);
        if (status == HttpStatusCode.OK && json?["content"] is JsonValue content)
            return new FetchedPage(url, json["statusCode"]?.GetValue<int>() ?? 200, json["contentType"]?.GetValue<string>() ?? "",
                content.GetValue<string>());
        var msg = json?["message"]?.GetValue<string>() ?? json?["error"]?.GetValue<string>();
        throw status switch
        {
            HttpStatusCode.Unauthorized => new WebServiceException("Browserbase rejected the API key. Check it in Settings → Web research."),
            HttpStatusCode.Forbidden => new WebServiceException($"Browserbase couldn't access {target.Host}" + (msg is null ? "." : $": {msg}")),
            HttpStatusCode.PaymentRequired => new WebServiceException("Browserbase's free-plan quota for page reading is used up."),
            HttpStatusCode.TooManyRequests => new WebServiceException("Browserbase's concurrent request limit was reached.", transient: true),
            HttpStatusCode.BadRequest => new WebServiceException($"Browserbase couldn't convert {target.Host} to text" + (msg is null ? "." : $": {msg}")),
            HttpStatusCode.BadGateway => new WebServiceException($"{target.Host} was too large or its certificate couldn't be verified."),
            HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout => new WebServiceException($"Browserbase couldn't load {target.Host} in time.", transient: true),
            _ => new WebServiceException($"Browserbase error (HTTP {(int)status})" + (msg is null ? "." : $": {msg}")),
        };
    }

    public void Dispose() => _http.Dispose();
}
