using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SlothBoss.Core.Actions;
using SlothBoss.Core.Documents;
using SlothBoss.Core.Storage;

namespace SlothBoss.Core.Ai;

public static class Prompts
{
    public static string System => Resources.Read("prompts.system.md");

    /// <summary>Fills {{name}} placeholders in a single pass, so substituted values are never re-expanded.</summary>
    public static string Render(string name, IReadOnlyDictionary<string, string> values) =>
        Regex.Replace(Resources.Read($"prompts.{name}.md"), @"\{\{(\w+)\}\}",
            m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    public static JsonNode OutputSchema(string name) =>
        JsonNode.Parse(Resources.Read("model-output.schema.json"))!["$defs"]![name]!.DeepClone();
}

public sealed record AskResult(
    string Status, string Answer, IReadOnlyList<Source> Citations, IReadOnlyList<string> Inferences,
    Evidence Evidence, int InvalidCitations, IReadOnlyList<string> Notices, bool UsedModel, TimeSpan Elapsed);

public sealed record ProposalBatch(
    string BatchId, IReadOnlyList<ActionRecord> Proposals, IReadOnlyList<string> Notices,
    string? Summary, IReadOnlyList<Source> Citations, Evidence Evidence, TimeSpan Elapsed);

/// <summary>
/// The AI features. Every method retrieves local evidence, calls the local model, validates the
/// output, and either returns text or stores *proposals* — it never mutates pages or tasks itself.
/// </summary>
public sealed class AssistantService(Workspace ws, OllamaClient client, string model)
{
    public const int MaxAttempts = 3;

    public string Model { get; } = model;
    private readonly Retriever _retriever = new(ws);

    private async Task<(T Value, TimeSpan Elapsed)> Structured<T>(string userPrompt, string schema, Func<string, T> parse, CancellationToken ct)
    {
        var messages = new List<ChatMessage> { new("system", Prompts.System), new("user", userPrompt) };
        var format = Prompts.OutputSchema(schema);
        var elapsed = TimeSpan.Zero;
        string? lastError = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var reply = await client.ChatAsync(new ChatRequest(Model, messages, format), null, ct).ConfigureAwait(false);
            elapsed += reply.Elapsed;
            try
            {
                return (parse(reply.Content), elapsed);
            }
            catch (ModelOutputException e)
            {
                lastError = e.Message;
                messages.Add(new("assistant", reply.Content.Length > 2000 ? reply.Content[..2000] : reply.Content));
                messages.Add(new("user", $"That reply was invalid: {e.Message} Reply again with only the corrected JSON object."));
            }
        }
        throw new ModelOutputException($"The model did not return a valid response after {MaxAttempts} attempts ({lastError}). Nothing was changed.");
    }

    // ---- Ask ------------------------------------------------------------------------------

    public async Task<AskResult> AskAsync(string question, string? pinnedPageId = null, CancellationToken ct = default)
    {
        question = question.Trim();
        if (question.Length == 0) throw new SlothBossException("Type a question first.");
        var evidence = _retriever.Retrieve(question, pinnedPageId);
        if (evidence.IsEmpty)
        {
            return new AskResult("not_found",
                "I couldn't find anything in your workspace related to that question, so I can't answer it from your notes.",
                [], [], evidence, 0, ["No matching pages or tasks were found; the model was not called."], false, TimeSpan.Zero);
        }

        var prompt = Prompts.Render("ask", new Dictionary<string, string> { ["evidence"] = evidence.Format(), ["question"] = question });
        var (output, elapsed) = await Structured(prompt, "ask", ModelOutput.ParseAsk, ct).ConfigureAwait(false);

        var check = ModelOutput.ValidateCitations(output.Answer, output.Citations, evidence);
        var notices = new List<string>();
        var status = output.Status;
        if (check.InvalidCount > 0)
            notices.Add($"Removed {check.InvalidCount} citation(s) to sources that were not retrieved.");
        if (status != "not_found" && check.ValidIds.Count == 0)
        {
            notices.Add("The model did not cite any retrieved source. Treat this answer as unverified.");
            if (status == "answered") status = "partial";
        }
        if (evidence.Truncated) notices.Add("Some matching material did not fit in the model's context.");
        var cited = check.ValidIds.Select(id => evidence.Find(id)!).ToList();
        return new AskResult(status, check.Text, cited, output.Inferences, evidence, check.InvalidCount, notices, true, elapsed);
    }

    // ---- Summarize ------------------------------------------------------------------------

    public async Task<string> SummarizeAsync(string pageId, Action<string>? onDelta = null, CancellationToken ct = default)
    {
        var evidence = _retriever.ForPage(pageId);
        if (evidence.Sources[0].Excerpt.Trim().Length == 0) return "This page is empty, so there is nothing to summarize.";
        var prompt = Prompts.Render("summarize", new Dictionary<string, string> { ["evidence"] = evidence.Format() });
        var reply = await client.ChatAsync(new ChatRequest(Model, [new("system", Prompts.System), new("user", prompt)]), onDelta, ct).ConfigureAwait(false);
        var text = reply.Content.Trim();
        if (text.Length == 0) throw new ModelOutputException("The model returned an empty summary.");
        return evidence.Truncated ? text + "\n\n(Only the first part of this long page was summarized.)" : text;
    }

    // ---- Extract tasks --------------------------------------------------------------------

    public async Task<ProposalBatch> ExtractTasksAsync(string pageId, CancellationToken ct = default)
    {
        var page = ws.Pages.Get(pageId) ?? throw new SlothBossException("The page no longer exists.");
        var evidence = _retriever.ForPage(pageId);
        var batchId = Ids.New();
        if (page.ContentText.Trim().Length == 0)
            return new ProposalBatch(batchId, [], ["This page is empty."], null, [], evidence, TimeSpan.Zero);

        var prompt = Prompts.Render("extract_tasks", new Dictionary<string, string> { ["evidence"] = evidence.Format(), ["today"] = Clock.Today() });
        var (output, elapsed) = await Structured(prompt, "extract_tasks", ModelOutput.ParseExtract, ct).ConfigureAwait(false);

        var notices = new List<string>();
        var existing = ws.Tasks.ForPage(pageId).Select(t => Normalize(t.Title)).ToHashSet();
        var pageText = Normalize(page.ContentText);
        var proposals = new List<ActionRecord>();
        foreach (var t in output.Tasks.Take(8))
        {
            var payload = BuildTask(t.Title, t.Description, t.Priority, t.DueDate, pageId, notices);
            if (payload is null) continue;
            if (!existing.Add(Normalize(payload.Title))) { notices.Add($"Skipped “{payload.Title}”: a task with that title already exists for this page."); continue; }
            var quote = t.Evidence.Trim().Trim('"', '“', '”');
            var verified = quote.Length > 0 && pageText.Contains(Normalize(quote));
            var summary = Describe(payload, page.DisplayTitle) +
                          (quote.Length == 0 ? "\nNo supporting quote given." :
                           verified ? $"\nFrom the page: “{Clip(quote, 160)}”" :
                           $"\nModel quote not found on the page: “{Clip(quote, 160)}”");
            proposals.Add(ws.Actions.Propose(batchId, payload, summary, Model));
        }
        if (output.Tasks.Count == 0) notices.Add("The model found no actionable tasks on this page.");
        return new ProposalBatch(batchId, proposals, notices, null, [], evidence, elapsed);
    }

    // ---- Plan tasks across the workspace ---------------------------------------------------

    public async Task<ProposalBatch> PlanTasksAsync(string request, string? pinnedPageId = null, CancellationToken ct = default)
    {
        request = request.Trim();
        if (request.Length == 0) throw new SlothBossException("Describe what you want reviewed first.");
        var evidence = _retriever.Retrieve(request, pinnedPageId);
        var batchId = Ids.New();
        if (evidence.IsEmpty)
            return new ProposalBatch(batchId, [], ["No matching pages or tasks were found; the model was not called."],
                "I couldn't find notes related to that request.", [], evidence, TimeSpan.Zero);

        var prompt = Prompts.Render("plan_tasks", new Dictionary<string, string> { ["evidence"] = evidence.Format(), ["question"] = request });
        var (output, elapsed) = await Structured(prompt, "plan_tasks", ModelOutput.ParsePlan, ct).ConfigureAwait(false);

        var notices = new List<string>();
        var check = ModelOutput.ValidateCitations(output.Summary, [], evidence);
        if (check.InvalidCount > 0) notices.Add($"Removed {check.InvalidCount} citation(s) to sources that were not retrieved.");

        var proposals = new List<ActionRecord>();
        var titles = new HashSet<string>();
        foreach (var t in output.Tasks.Take(8))
        {
            var source = evidence.Find(t.Source.Trim('[', ']', ' '));
            if (source is null)
                notices.Add($"“{Clip(t.Title, 60)}” cited an unknown source ({(t.Source.Length == 0 ? "none" : t.Source)}), so it is not linked to a page.");
            var payload = BuildTask(t.Title, string.IsNullOrWhiteSpace(t.Rationale) ? t.Description : $"{t.Description}\nWhy: {t.Rationale}",
                t.Priority, "", source?.PageId, notices);
            if (payload is null || !titles.Add(Normalize(payload.Title))) continue;
            var linkedTitle = source?.PageId is null ? null : ws.Pages.Get(source.PageId)?.DisplayTitle;
            var summary = Describe(payload, linkedTitle) + (source is null ? "" : $"\nSource: [{source.Id}] {source.DisplayTitle}");
            proposals.Add(ws.Actions.Propose(batchId, payload, summary, Model));
        }
        var cited = check.ValidIds.Select(id => evidence.Find(id)!).ToList();
        return new ProposalBatch(batchId, proposals, notices, check.Text, cited, evidence, elapsed);
    }

    // ---- Draft a note ---------------------------------------------------------------------

    public async Task<ProposalBatch> DraftNoteAsync(string request, string? pinnedPageId = null, CancellationToken ct = default)
    {
        request = request.Trim();
        if (request.Length == 0) throw new SlothBossException("Describe the note you want drafted first.");
        var evidence = _retriever.Retrieve(request, pinnedPageId);
        var batchId = Ids.New();
        if (evidence.IsEmpty)
            return new ProposalBatch(batchId, [], ["No matching pages or tasks were found; the model was not called."],
                "I couldn't find material for that note in your workspace.", [], evidence, TimeSpan.Zero);

        var prompt = Prompts.Render("draft_note", new Dictionary<string, string> { ["evidence"] = evidence.Format(), ["question"] = request });
        var (output, elapsed) = await Structured(prompt, "draft_note", ModelOutput.ParseDraft, ct).ConfigureAwait(false);

        var notices = new List<string>();
        var check = ModelOutput.ValidateCitations(output.Markdown, output.Citations, evidence);
        if (check.InvalidCount > 0) notices.Add($"Removed {check.InvalidCount} citation(s) to sources that were not retrieved.");
        var doc = Markdown.Parse(check.Text);
        LinkCitationMarkers(doc, evidence);
        var citedPages = check.ValidIds.Select(id => evidence.Find(id)!).Where(s => s.PageId is not null).DistinctBy(s => s.PageId).ToList();
        if (citedPages.Count > 0)
        {
            doc.Blocks.Add(Block.Heading(2, "Sources"));
            foreach (var s in citedPages)
            {
                var title = ws.Pages.Get(s.PageId!)?.DisplayTitle ?? s.DisplayTitle;
                doc.Blocks.Add(new Block(BlockType.Bulleted, new Span($"[{s.Id}] "), new Span(title) { Link = Links.ForPage(s.PageId!) }));
            }
        }
        else notices.Add("The draft does not cite any retrieved source.");

        var title2 = Clip(output.Title.Replace('\n', ' '), PageStore.MaxTitleLength);
        var payload = new CreatePagePayload(title2, null, doc.Normalize());
        var summary = $"Create page “{title2}” ({doc.Blocks.Count} blocks, {citedPages.Count} linked source page(s))";
        var proposal = ws.Actions.Propose(batchId, payload, summary, Model);
        return new ProposalBatch(batchId, [proposal], notices, null, check.ValidIds.Select(id => evidence.Find(id)!).ToList(), evidence, elapsed);
    }

    /// <summary>Turns validated [S1] markers in drafted text into links to the source page.</summary>
    private static void LinkCitationMarkers(PageDocument doc, Evidence evidence)
    {
        var rx = new Regex(@"\[(S\d+)\]");
        foreach (var block in doc.Blocks.Where(b => b.Type != BlockType.Code))
        {
            var spans = new List<Span>();
            foreach (var span in block.Spans)
            {
                if (span.Link is not null || span.Code) { spans.Add(span); continue; }
                var last = 0;
                foreach (Match m in rx.Matches(span.Text))
                {
                    var src = evidence.Find(m.Groups[1].Value);
                    if (src?.PageId is null) continue;
                    if (m.Index > last) spans.Add(new Span { Text = span.Text[last..m.Index], Bold = span.Bold, Italic = span.Italic });
                    spans.Add(new Span { Text = m.Value, Link = Links.ForPage(src.PageId) });
                    last = m.Index + m.Length;
                }
                if (last < span.Text.Length) spans.Add(new Span { Text = span.Text[last..], Bold = span.Bold, Italic = span.Italic });
            }
            block.Spans = spans;
        }
    }

    // ---- helpers --------------------------------------------------------------------------

    private CreateTaskPayload? BuildTask(string title, string description, string priority, string due, string? pageId, List<string> notices)
    {
        title = title.Replace('\n', ' ').Trim().TrimEnd('.');
        if (title.Length == 0) { notices.Add("Skipped a task with an empty title."); return null; }
        if (title.Length > TaskStore.MaxTitleLength) title = title[..TaskStore.MaxTitleLength];
        var pr = priority.Trim().ToLowerInvariant();
        if (!TaskPriority.IsValid(pr)) pr = TaskPriority.Medium;
        string? dueDate = due.Trim();
        if (dueDate.Length == 0) dueDate = null;
        else if (!Clock.IsValidDate(dueDate)) { notices.Add($"Ignored an invalid due date “{Clip(dueDate, 20)}” for “{Clip(title, 60)}”."); dueDate = null; }
        var desc = string.IsNullOrWhiteSpace(description) ? null : Clip(description.Trim(), TaskStore.MaxDescriptionLength);
        if (pageId is not null && !ws.Pages.Exists(pageId)) pageId = null;
        try
        {
            var payload = new CreateTaskPayload(title, desc, pr, dueDate, pageId);
            payload.Validate();
            return payload;
        }
        catch (ActionValidationException e)
        {
            notices.Add($"Skipped an invalid task proposal: {e.Message}");
            return null;
        }
    }

    private static string Describe(CreateTaskPayload p, string? pageTitle)
    {
        var parts = new List<string> { $"Create task “{p.Title}”", $"{TaskPriority.Label(p.Priority)} priority" };
        if (p.DueDate is not null) parts.Add($"due {p.DueDate}");
        parts.Add(pageTitle is null ? "not linked to a page" : $"linked to “{pageTitle}”");
        return string.Join(" · ", parts);
    }

    private static string Normalize(string s) => Regex.Replace(s.ToLowerInvariant(), @"\s+", " ").Trim();
    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
