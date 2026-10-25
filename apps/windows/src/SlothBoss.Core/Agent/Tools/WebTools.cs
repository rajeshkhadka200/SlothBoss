using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SlothBoss.Core.Agent.Web;
using SlothBoss.Core.Ai;
using SlothBoss.Core.Documents;

namespace SlothBoss.Core.Agent.Tools;

/// <summary>web.search — real Google results via SerpApi. Sends only the query text.</summary>
public sealed class WebSearchTool(WebConfig web) : IAgentTool
{
    public ToolSpec Spec { get; } = new(
        "web.search", 1, "Search the web (Google via SerpApi) for current information. Sends the query to SerpApi.",
        [
            new ToolArg("query", "string", "Short search query", null),
            new ToolArg("max_results", "integer", "How many results", 6, Min: 1, Max: 10),
        ],
        "web_results: [{id W#, title, url, snippet, source, query}]",
        ToolRisk.Read, ToolScope.Workspace, TimeSpan.FromSeconds(60), 2, Idempotent: true, UsesModel: false,
        "Each result has a real http(s) URL returned by SerpApi.", [], ["web_results"]);

    public string? UnavailableReason => web.SearchUnavailableReason;
    public bool SendsDataOnline => true;

    public async Task<ToolOutcome> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        if (UnavailableReason is { } reason) return ToolOutcome.Failure(reason);
        var query = ToolArgs.Str(args, "query");
        if (query.Length == 0) query = ctx.Run.Goal.Length > 120 ? ctx.Run.Goal[..120] : ctx.Run.Goal;
        query = Regex.Replace(query, @"\s+", " ").Trim();
        using var client = new SerpApiClient(web.SerpApiKey()!, web.Handler);
        IReadOnlyList<SearchResult> results;
        try { results = await client.SearchAsync(query, ToolArgs.Int(args, "max_results", 6), ct).ConfigureAwait(false); }
        catch (WebServiceException e) { return ToolOutcome.Failure(e.Message, e.Transient); }

        var all = ctx.ArtifactArray("web_results");
        var known = all.OfType<JsonNode>().Select(r => r["url"]!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var r in results.Where(r => known.Add(r.Url)))
        {
            all.Add(new JsonObject
            {
                ["id"] = $"W{all.Count + 1}", ["title"] = r.Title, ["url"] = r.Url, ["snippet"] = r.Snippet,
                ["source"] = r.Source, ["query"] = query, ["read"] = false,
            });
            added++;
        }
        ctx.Artifacts["web_results"] = all;
        ctx.Log("info", $"Searched the web for “{query}” (SerpApi): {results.Count} result(s), {added} new.");
        if (results.Count == 0) return ToolOutcome.Failure($"The search for “{query}” returned no results.");
        return ToolOutcome.Success($"Found {results.Count} result(s) for “{query}”: " +
            string.Join("; ", results.Take(3).Select(r => r.Source ?? new Uri(r.Url).Host)) + (results.Count > 3 ? "…" : ""),
            new JsonObject { ["query"] = query, ["results"] = results.Count, ["new"] = added });
    }

    public IReadOnlyList<VerificationCheck> Verify(ToolContext ctx, JsonObject args, ToolOutcome outcome)
    {
        var results = ctx.ArtifactArray("web_results").OfType<JsonNode>().ToList();
        var bad = results.Count(r => !Uri.TryCreate(r["url"]!.GetValue<string>(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"));
        return [new("Search results are real web links", results.Count > 0 && bad == 0, $"{results.Count} result(s) with source URLs")];
    }
}

/// <summary>
/// web.read — reads the best search results' pages via the Browserbase Fetch API (as Markdown). It can only
/// read URLs that came back from web.search, never an address taken from a page or chosen freely by the model.
/// </summary>
public sealed class WebReadTool(WebConfig web) : IAgentTool
{
    public const int MaxCharsPerPage = 6000;

    public ToolSpec Spec { get; } = new(
        "web.read", 1, "Read the full text of the top search results (via Browserbase) to check facts. Sends the page addresses to Browserbase.",
        [new ToolArg("max_pages", "integer", "How many result pages to read", 3, Min: 1, Max: 5)],
        "web_pages: [{id, url, title, content, suspicious}]",
        ToolRisk.Read, ToolScope.Workspace, TimeSpan.FromMinutes(3), 1, Idempotent: true, UsesModel: false,
        "Each read page came from the search results and returned text.", ["web_results"], ["web_pages"]);

    public string? UnavailableReason => web.ReadUnavailableReason;
    public bool SendsDataOnline => true;

    public async Task<ToolOutcome> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        if (UnavailableReason is { } reason) return ToolOutcome.Failure(reason);
        var results = ctx.ArtifactArray("web_results").OfType<JsonObject>().ToList();
        if (results.Count == 0) return ToolOutcome.Failure("There are no search results to read.");
        var pages = ctx.ArtifactArray("web_pages");
        var already = pages.OfType<JsonNode>().Select(p => p["id"]!.GetValue<string>()).ToHashSet();

        // Prefer different websites over several pages from the same one.
        var max = ToolArgs.Int(args, "max_pages", 3);
        var picked = results.Where(r => !already.Contains(r["id"]!.GetValue<string>()))
            .GroupBy(r => new Uri(r["url"]!.GetValue<string>()).Host).Select(g => g.First()).Take(max).ToList();

        using var client = new BrowserbaseClient(web.BrowserbaseKey()!, web.Handler);
        var errors = new List<string>();
        var transient = 0;
        foreach (var r in picked)
        {
            ct.ThrowIfCancellationRequested();
            ctx.CheckStop();
            var url = r["url"]!.GetValue<string>();
            try
            {
                var page = await client.FetchMarkdownAsync(url, ct).ConfigureAwait(false);
                var text = Regex.Replace(page.Content, @"\n{3,}", "\n\n").Trim();
                if (text.Length < 200) { errors.Add($"{new Uri(url).Host}: almost no readable text"); continue; }
                var injection = InjectionHeuristics.Check(text);
                if (injection.Count > 0) ctx.Log("warning", $"{new Uri(url).Host} contains text aimed at an AI ({string.Join(", ", injection)}); treated only as data.");
                pages.Add(new JsonObject
                {
                    ["id"] = r["id"]!.GetValue<string>(), ["url"] = url, ["title"] = r["title"]!.GetValue<string>(),
                    ["content"] = text.Length > MaxCharsPerPage ? text[..MaxCharsPerPage] : text,
                    ["truncated"] = text.Length > MaxCharsPerPage, ["suspicious"] = injection.Count > 0,
                });
                r["read"] = true;
            }
            catch (WebServiceException e)
            {
                errors.Add(e.Message);
                if (e.Transient) transient++;
            }
        }
        ctx.Artifacts["web_pages"] = pages;
        ctx.Artifacts["web_read_errors"] = new JsonArray([.. errors.Select(e => (JsonNode)e)]);
        var read = picked.Count - errors.Count;
        if (read <= 0)
            return ToolOutcome.Failure("No pages could be read: " + string.Join(" ", errors.Take(3)), transient: transient == errors.Count && errors.Count > 0);
        return ToolOutcome.Success($"Read {read} page(s) via Browserbase" + (errors.Count > 0 ? $"; {errors.Count} couldn't be read" : "") + ".",
            new JsonObject { ["read"] = read, ["failed"] = errors.Count });
    }

    public IReadOnlyList<VerificationCheck> Verify(ToolContext ctx, JsonObject args, ToolOutcome outcome)
    {
        var urls = ctx.ArtifactArray("web_results").OfType<JsonNode>().Select(r => r["url"]!.GetValue<string>()).ToHashSet();
        var pages = ctx.ArtifactArray("web_pages").OfType<JsonNode>().ToList();
        var foreign = pages.Count(p => !urls.Contains(p["url"]!.GetValue<string>()));
        return [new("Read pages came from the search results and contain text", pages.Count > 0 && foreign == 0, $"{pages.Count} page(s) read")];
    }
}

/// <summary>
/// research.report — Gemma writes a cited report from the retrieved sources. Citations are checked against
/// what was actually retrieved; each claim's label is decided by the evidence, not by the model's say-so.
/// </summary>
public sealed class ResearchReportTool : IAgentTool
{
    public ToolSpec Spec { get; } = new(
        "research.report", 1, "Write a cited research report from the web sources found, and save it as a page (optionally also as a .md file in the folder).",
        [new ToolArg("save_copy_in_folder", "boolean", "Also save the report as a .md file in the approved folder", false)],
        "research_report: {page_id, title, markdown, findings, sources}",
        ToolRisk.Create, ToolScope.Workspace, TimeSpan.FromMinutes(5), 1, Idempotent: false, UsesModel: true,
        "Every citation refers to a retrieved source; the page is read back.", ["web_results"], ["research_report"]);

    private const int EvidenceBudget = 14000;

    public async Task<ToolOutcome> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var results = ctx.ArtifactArray("web_results").OfType<JsonObject>().ToList();
        if (results.Count == 0) return ToolOutcome.Failure("There are no web sources to write a report from.");
        var pages = ctx.ArtifactArray("web_pages").OfType<JsonNode>().ToDictionary(p => p["id"]!.GetValue<string>());

        // Evidence block: read pages first (with text), then snippets, within a size budget.
        var sb = new StringBuilder();
        var included = new List<JsonObject>();
        foreach (var r in results.OrderByDescending(r => pages.ContainsKey(r["id"]!.GetValue<string>())))
        {
            var id = r["id"]!.GetValue<string>();
            var entry = new StringBuilder();
            entry.Append('[').Append(id).Append("] ").Append(pages.ContainsKey(id) ? "page read" : "snippet only").Append(": \"")
                 .Append(Retriever.Sanitize(r["title"]!.GetValue<string>())).Append("\" (").Append(new Uri(r["url"]!.GetValue<string>()).Host).Append(")\n");
            entry.Append(Retriever.Sanitize(r["snippet"]!.GetValue<string>())).Append('\n');
            if (pages.TryGetValue(id, out var readPage))
            {
                var content = readPage["content"]!.GetValue<string>();
                entry.Append(Retriever.Sanitize(content.Length > 3000 ? content[..3000] : content)).Append('\n');
            }
            if (sb.Length + entry.Length > EvidenceBudget && included.Count > 0) continue;
            sb.Append(entry).Append('\n');
            included.Add(r);
        }
        var evidence = new Evidence(included.Select(r => new Source(r["id"]!.GetValue<string>(), "web", r["url"]!.GetValue<string>(),
            r["title"]!.GetValue<string>(), r["snippet"]!.GetValue<string>(), null)).ToList(), included.Count < results.Count);

        var prompt = Prompts.Render("agent_research_report", new Dictionary<string, string> { ["goal"] = Retriever.Sanitize(ctx.Run.Goal), ["sources"] = sb.ToString() });
        var report = await ctx.AskModelAsync(prompt, "agent_research_report", ParseReport, ct).ConfigureAwait(false);

        // Validate citations and decide each claim's label from the evidence.
        var invalid = 0;
        var summary = ModelOutput.ValidateCitations(report.Summary, [], evidence);
        invalid += summary.InvalidCount;
        var recommendation = ModelOutput.ValidateCitations(report.Recommendation, [], evidence);
        invalid += recommendation.InvalidCount;
        var findings = new JsonArray();
        var cited = new HashSet<string>(summary.ValidIds.Concat(recommendation.ValidIds));
        foreach (var f in report.Findings.Take(8))
        {
            var check = ModelOutput.ValidateCitations(f.Claim, f.Sources, evidence);
            invalid += check.InvalidCount;
            foreach (var id in check.ValidIds) cited.Add(id);
            var readSupport = check.ValidIds.Any(pages.ContainsKey);
            var label = check.ValidIds.Count == 0 ? "unsupported"
                : f.Confidence == "uncertain" ? "uncertain"
                : readSupport ? (f.Confidence == "verified" ? "verified" : "likely")
                : "snippet";
            findings.Add(new JsonObject
            {
                ["claim"] = Regex.Replace(check.Text, @"\s*\[(W\d+(?:,\s*W\d+)*)\]", "").Trim(),
                ["sources"] = new JsonArray([.. check.ValidIds.Select(i => (JsonNode)i)]),
                ["label"] = label,
            });
        }

        var md = Render(ctx, report.Title, summary.Text, findings, recommendation.Text, report.Caveats, results, pages, cited, invalid);
        var title = string.IsNullOrWhiteSpace(report.Title) ? "Research report" : report.Title.Trim();
        if (title.Length > 120) title = title[..120];
        var page = ctx.Ws.Pages.Create($"{title} [#{ctx.Run.Id[..6]}]", null, Markdown.Parse(md));
        var artifact = new JsonObject
        {
            ["page_id"] = page.Id, ["page_updated_at"] = page.UpdatedAt, ["title"] = title, ["markdown"] = md,
            ["findings"] = findings.DeepClone(), ["invalid_citations"] = invalid,
            ["sources_cited"] = cited.Count, ["sources_read"] = pages.Count,
        };
        if (ToolArgs.Bool(args, "save_copy_in_folder") && ctx.MaybeSandbox is { } sb2)
        {
            var rel = sb2.UniqueTarget(BackupNameFor(title), new HashSet<string>());
            var op = ctx.Ws.Agent.AddFileOp(ctx.Run.Id, ctx.Step.Id, "create_file", null, rel, null, null, "Research report copy");
            ctx.Ws.Agent.UpdateOp(op.Id, "running");
            sb2.CreateNewFile(rel, md);
            ctx.Ws.Agent.SetOpHash(op.Id, sb2.HashRel(rel));
            ctx.Ws.Agent.UpdateOp(op.Id, "applied");
            artifact["file"] = rel;
        }
        ctx.Artifacts["research_report"] = artifact;
        var verified = findings.Count(f => f!["label"]!.GetValue<string>() == "verified");
        return ToolOutcome.Success($"Wrote “{title}”: {findings.Count} finding(s) ({verified} verified in a page read), citing {cited.Count} source(s)" +
            (invalid > 0 ? $"; removed {invalid} citation(s) to sources that weren't retrieved" : "") + ".",
            new JsonObject { ["findings"] = findings.Count, ["verified"] = verified, ["sources"] = cited.Count, ["invalid_citations"] = invalid });
    }

    private static string BackupNameFor(string title) =>
        (FileSandbox.SafeSegment(title, 70) ?? "Research report") + ".md";

    public static string LabelText(string label) => label switch
    {
        "verified" => "confirmed in a source page",
        "likely" => "supported by a source page",
        "snippet" => "from a search snippet only — not confirmed",
        "uncertain" => "uncertain",
        _ => "no valid source — treat as unverified",
    };

    private static string Render(ToolContext ctx, string title, string summary, JsonArray findings, string recommendation, IReadOnlyList<string> caveats,
        List<JsonObject> results, Dictionary<string, JsonNode> pages, HashSet<string> cited, int invalid)
    {
        var sb = new StringBuilder();
        sb.Append(summary).Append("\n\n## Findings\n");
        foreach (var f in findings)
        {
            var ids = f!["sources"]!.AsArray().Select(i => i!.GetValue<string>()).ToList();
            sb.Append("- ").Append(Markdown.EscapeText(f["claim"]!.GetValue<string>()))
              .Append(ids.Count > 0 ? " [" + string.Join(", ", ids) + "]" : "")
              .Append(" — *").Append(LabelText(f["label"]!.GetValue<string>())).Append("*\n");
        }
        sb.Append("\n## Recommendation\n").Append(recommendation).Append('\n');
        var notes = caveats.ToList();
        if (invalid > 0) notes.Add($"{invalid} citation(s) the model gave did not match any retrieved source and were removed.");
        if (notes.Count > 0)
        {
            sb.Append("\n## Caveats\n");
            foreach (var c in notes) sb.Append("- ").Append(Markdown.EscapeText(c)).Append('\n');
        }
        sb.Append("\n## Sources\n");
        foreach (var r in results.Where(r => cited.Contains(r["id"]!.GetValue<string>())))
        {
            var id = r["id"]!.GetValue<string>();
            sb.Append("- [").Append(id).Append("] [").Append(Markdown.EscapeText(r["title"]!.GetValue<string>())).Append("](")
              .Append(r["url"]!.GetValue<string>().Replace(")", "%29").Replace(" ", "%20")).Append(") — ")
              .Append(pages.ContainsKey(id) ? "page read" : "search result").Append('\n');
        }
        sb.Append($"\nWritten locally by {ctx.Model?.DisplayName ?? "the local model"} on {DateTime.Now:yyyy-MM-dd} from {results.Count} search result(s) (SerpApi) " +
                  $"and {pages.Count} page(s) read (Browserbase). Check important facts in the sources.\n");
        return sb.ToString();
    }

    private sealed record Finding(string Claim, List<string> Sources, string Confidence);
    private sealed record Report(string Title, string Summary, List<Finding> Findings, string Recommendation, List<string> Caveats);

    private static Report ParseReport(string text)
    {
        var o = ModelOutput.ExtractJson(text);
        static string S(JsonObject o, string k, bool required = true) =>
            o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : required ? throw new ModelOutputException($"Missing '{k}'.") : "";
        static List<string> L(JsonObject o, string k) =>
            o[k] is JsonArray a ? a.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "").Where(s => s.Length > 0).ToList() : [];
        var summary = S(o, "summary");
        if (summary.Length < 20) throw new ModelOutputException("The summary was empty.");
        if (o["findings"] is not JsonArray fa) throw new ModelOutputException("Missing 'findings' list.");
        var findings = fa.OfType<JsonObject>().Select(f => new Finding(S(f, "claim"), L(f, "sources"),
            S(f, "confidence", false) is var c && c is "verified" or "likely" or "uncertain" ? c : "uncertain")).Where(f => f.Claim.Length > 0).ToList();
        return new Report(S(o, "title", false), summary, findings, S(o, "recommendation", false), L(o, "caveats"));
    }

    public IReadOnlyList<VerificationCheck> Verify(ToolContext ctx, JsonObject args, ToolOutcome outcome)
    {
        var r = ctx.Artifacts["research_report"];
        var page = r?["page_id"]?.GetValue<string>() is { } id ? ctx.Ws.Pages.Get(id) : null;
        var ids = ctx.ArtifactArray("web_results").OfType<JsonNode>().Select(x => x["id"]!.GetValue<string>()).ToHashSet();
        var orphan = (r?["findings"] as JsonArray ?? []).SelectMany(f => f!["sources"]!.AsArray()).Count(s => !ids.Contains(s!.GetValue<string>()));
        var checks = new List<VerificationCheck>
        {
            new("Every citation refers to a retrieved source", orphan == 0, orphan == 0 ? $"{r?["sources_cited"]} source(s) cited" : $"{orphan} unknown citation(s)"),
            new("Report saved and readable", page is not null && page.ContentText.Contains("Sources"), page?.DisplayTitle ?? "Page not found"),
        };
        if (r?["file"]?.GetValue<string>() is { } file && ctx.MaybeSandbox is { } sb)
            checks.Add(new("Report copy saved in the folder", sb.FileExists(file), file));
        return checks;
    }

    public IReadOnlyList<string> Undo(ToolContext ctx)
    {
        var notes = new List<string>();
        var r = ctx.Artifacts["research_report"];
        if (r?["page_id"]?.GetValue<string>() is { } id && ctx.Ws.Pages.Get(id) is { } page)
        {
            if (page.UpdatedAt == r["page_updated_at"]!.GetValue<string>()) ctx.Ws.Pages.Delete(id);
            else notes.Add("The research report page was edited, so it was kept.");
        }
        foreach (var o in ctx.Ws.Agent.FileOpsForStep(ctx.Step.Id).Where(o => o.Op == "create_file" && o.Status == "applied"))
        {
            if (ctx.MaybeSandbox is { } sb && sb.FileExists(o.TargetRel) && sb.HashRel(o.TargetRel) == o.Sha)
            {
                File.Delete(sb.Resolve(o.TargetRel));
                ctx.Ws.Agent.UpdateOp(o.Id, "undone");
            }
            else notes.Add("The report file in the folder was changed, so it was kept.");
        }
        return notes;
    }
}
