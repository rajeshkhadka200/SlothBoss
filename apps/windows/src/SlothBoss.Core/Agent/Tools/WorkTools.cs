using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SlothBoss.Core.Agent.Web;
using SlothBoss.Core.Ai;
using SlothBoss.Core.Documents;
using SlothBoss.Core.Storage;

namespace SlothBoss.Core.Agent.Tools;

internal static class DocSelection
{
    public static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), @"\s+", " ").Trim();

    /// <summary>
    /// Paths are shown to the model with "/" separators: models copy them into JSON without escaping,
    /// and a Windows "\t" or "\r" would silently become a tab or carriage return.
    /// </summary>
    public static string ForModel(string relPath) => Retriever.Sanitize(relPath.Replace('\\', '/'));

    /// <summary>Reads any supported file (documents, slides, spreadsheets, images via the vision model).</summary>
    public static Task<ExtractedText?> ReadAsync(ToolContext ctx, string path, List<string> errors, CancellationToken ct, int maxChars = 12000) =>
        ContentReader.ReadAsync(ctx, path, errors, maxChars, ct);

    /// <summary>A short preview used to decide what's important: text for documents, the cached description (or just "image") for pictures.</summary>
    private static string Preview(ToolContext ctx, string path)
    {
        if (DocumentText.IsImage(Path.GetExtension(path)))
            return (ctx.Artifacts["image_descriptions"] as JsonObject)?.Where(kv => kv.Key.StartsWith(path + "|", StringComparison.Ordinal))
                       .Select(kv => kv.Value?.GetValue<string>()).FirstOrDefault() ?? "(an image)";
        try { return DocumentText.Extract(ctx.Sandbox.Resolve(path), 300).Text.Replace('\n', ' '); }
        catch (Exception e) when (e is SlothBossException or IOException) { return "(unreadable)"; }
    }

    /// <summary>Chooses documents for a goal. "important" asks the model to rank candidates by their opening text.</summary>
    public static async Task<(List<string> Paths, string? Reason)> ChooseAsync(ToolContext ctx, string selection, int max, CancellationToken ct)
    {
        var docs = Inventory.Documents(ctx.ArtifactArray("inventory"))
            .Where(n => !Inventory.Path(n).StartsWith(PlanOrganizationTool.DuplicatesFolder + "\\", StringComparison.OrdinalIgnoreCase))
            .Select(Inventory.Path).ToList();
        if (selection == "pdfs") docs = docs.Where(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)).ToList();
        if (selection != "important" || docs.Count <= max) return (docs.Take(max).ToList(), null);

        var sb = new StringBuilder();
        var candidates = docs.Take(30).ToList();
        foreach (var p in candidates)
        {
            ctx.CheckStop();
            var head = Preview(ctx, p);
            sb.Append("file: ").Append(ForModel(p)).Append('\n').Append(Retriever.Sanitize(head)).Append("\n\n");
        }
        var prompt = Prompts.Render("agent_select", new Dictionary<string, string>
            { ["goal"] = ctx.Run.Goal, ["documents"] = sb.ToString(), ["max"] = max.ToString() });
        var (files, reason) = await ctx.AskModelAsync(prompt, "agent_select", ParseSelect, ct).ConfigureAwait(false);
        var chosen = files.Select(f => MatchFile(candidates, f)).OfType<string>().Distinct().Take(max).ToList();
        if (chosen.Count == 0)
        {
            ctx.Log("warning", $"The model's choice ({string.Join(", ", files.Take(3).Select(f => $"“{Retriever.Sanitize(f)}”"))}) didn't match any real file; using the first documents instead.");
            chosen = candidates.Take(max).ToList();
        }
        return (chosen, reason);
    }

    /// <summary>
    /// Maps a model-written file reference onto a real candidate path: tolerant of "/" vs "\", quotes,
    /// a leading "file:" label, or a bare name — but it only ever returns paths from the candidate list.
    /// </summary>
    public static string? MatchFile(IReadOnlyList<string> candidates, string reference)
    {
        var r = reference.Trim().Trim('"', '\'', '“', '”', '`').Replace('/', '\\');
        if (r.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) r = r[5..].Trim();
        r = r.TrimStart('.', '\\');
        return candidates.FirstOrDefault(c => string.Equals(c, r, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(c => string.Equals(Path.GetFileName(c), Path.GetFileName(r), StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(c => string.Equals(Path.GetFileNameWithoutExtension(c), Path.GetFileNameWithoutExtension(r), StringComparison.OrdinalIgnoreCase));
    }

    private static (List<string>, string) ParseSelect(string text)
    {
        var o = ModelOutput.ExtractJson(text);
        if (o["files"] is not JsonArray arr) throw new ModelOutputException("Missing 'files' list.");
        return (arr.Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : throw new ModelOutputException("'files' must contain text.")).ToList(),
                o["reason"]?.GetValue<string>() ?? "");
    }
}

/// <summary>docs.summarize — summaries generated from the documents' actual text.</summary>
public sealed class SummarizeDocsTool : IAgentTool
{
    public ToolSpec Spec { get; } = new(
        "docs.summarize", 1, "Read documents, slides, spreadsheets and images (PDF, DOCX, PPTX, XLSX, TXT, MD, CSV, JPG, PNG) and summarize them with key facts.",
        [
            new ToolArg("selection", "string", "important: let the model pick; pdfs: only PDFs; all: every readable document", "important", ["important", "pdfs", "all"]),
            new ToolArg("max_documents", "integer", "How many documents at most", 4, Min: 1, Max: 8),
        ],
        "summaries: [{path, summary, key_points, document_type}]",
        ToolRisk.Read, ToolScope.ApprovedFolder, TimeSpan.FromMinutes(8), 1, Idempotent: true, UsesModel: true,
        "Each summary is non-empty and refers to a file that exists.", ["inventory"], ["summaries"]);

    public async Task<ToolOutcome> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var (paths, reason) = await DocSelection.ChooseAsync(ctx, ToolArgs.Str(args, "selection"), ToolArgs.Int(args, "max_documents", 4), ct).ConfigureAwait(false);
        if (paths.Count == 0) return ToolOutcome.Failure("There are no readable files (PDF, DOCX, PPTX, XLSX, TXT, MD, CSV or images) in the folder.");
        if (reason is { Length: > 0 }) ctx.Log("info", $"Chose {paths.Count} document(s): {reason}");
        var summaries = new JsonArray();
        var errors = new List<string>();
        foreach (var p in paths)
        {
            ct.ThrowIfCancellationRequested();
            ctx.CheckStop();
            var text = await DocSelection.ReadAsync(ctx, p, errors, ct).ConfigureAwait(false);
            if (text is null) continue;
            var injection = InjectionHeuristics.Check(text.Text);
            if (injection.Count > 0) ctx.Log("warning", $"“{p}” contains text aimed at an AI ({string.Join(", ", injection)}). It is treated only as data.");
            var prompt = Prompts.Render("agent_doc_summary", new Dictionary<string, string> { ["file"] = DocSelection.ForModel(p), ["text"] = Retriever.Sanitize(text.Text) });
            var s = await ctx.AskModelAsync(prompt, "agent_doc_summary", ParseSummary, ct).ConfigureAwait(false);
            summaries.Add(new JsonObject
            {
                ["path"] = p, ["summary"] = s.Summary, ["key_points"] = new JsonArray([.. s.KeyPoints.Take(5).Select(k => (JsonNode)k)]),
                ["document_type"] = s.Type, ["truncated"] = text.Truncated, ["suspicious"] = injection.Count > 0,
            });
        }
        ctx.Artifacts["summaries"] = summaries;
        ctx.Artifacts["summary_errors"] = new JsonArray([.. errors.Select(e => (JsonNode)e)]);
        if (summaries.Count == 0) return ToolOutcome.Failure("None of the chosen documents could be read: " + string.Join(" ", errors.Take(3)));
        return ToolOutcome.Success($"Summarized {summaries.Count} document(s)" + (errors.Count > 0 ? $"; {errors.Count} couldn't be read" : "") + ".",
            new JsonObject { ["summarized"] = summaries.Count, ["unreadable"] = errors.Count });
    }

    private static (string Summary, List<string> KeyPoints, string Type) ParseSummary(string text)
    {
        var o = ModelOutput.ExtractJson(text);
        var summary = o["summary"] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : throw new ModelOutputException("Missing 'summary'.");
        if (summary.Length < 10) throw new ModelOutputException("The summary was empty.");
        var points = o["key_points"] is JsonArray a ? a.Select(x => x?.GetValue<string>()?.Trim() ?? "").Where(x => x.Length > 0).ToList() : [];
        return (summary, points, o["document_type"]?.GetValue<string>()?.Trim() ?? "other");
    }

    public IReadOnlyList<VerificationCheck> Verify(ToolContext ctx, JsonObject args, ToolOutcome outcome)
    {
        var sums = ctx.ArtifactArray("summaries").OfType<JsonNode>().ToList();
        var missing = sums.Count(s => !File.Exists(ctx.Sandbox.Resolve(s["path"]!.GetValue<string>())));
        return [new("Summaries exist for real files", sums.Count > 0 && missing == 0, $"{sums.Count} summary(ies); {missing} source file(s) missing")];
    }
}

/// <summary>docs.extract_actions — action items with quotes checked against the document text.</summary>
public sealed class ExtractActionsTool : IAgentTool
{
    public ToolSpec Spec { get; } = new(
        "docs.extract_actions", 1, "Find things the user needs to do (with deadlines when stated) in documents, slides, spreadsheets and images.",
        [
            new ToolArg("selection", "string", "Which documents to read when none were summarized yet", "all", ["important", "pdfs", "all"]),
            new ToolArg("max_documents", "integer", "How many documents at most", 4, Min: 1, Max: 8),
        ],
        "actions: [{title, description, priority, due_date, source, quote, quote_verified}]",
        ToolRisk.Read, ToolScope.ApprovedFolder, TimeSpan.FromMinutes(8), 1, Idempotent: true, UsesModel: true,
        "Each action's supporting quote is looked up in the source document.", ["inventory"], ["actions"]);

    public async Task<ToolOutcome> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var summarized = ctx.ArtifactArray("summaries").OfType<JsonNode>().Select(s => s["path"]!.GetValue<string>()).ToList();
        var paths = summarized.Count > 0
            ? summarized.Take(ToolArgs.Int(args, "max_documents", 4)).ToList()
            : (await DocSelection.ChooseAsync(ctx, ToolArgs.Str(args, "selection"), ToolArgs.Int(args, "max_documents", 4), ct).ConfigureAwait(false)).Paths;
        if (paths.Count == 0) return ToolOutcome.Failure("There are no readable documents to look for action items in.");
        var actions = new JsonArray();
        var errors = new List<string>();
        var seen = new HashSet<string>();
        foreach (var p in paths)
        {
            ct.ThrowIfCancellationRequested();
            ctx.CheckStop();
            var text = await DocSelection.ReadAsync(ctx, p, errors, ct).ConfigureAwait(false);
            if (text is null) continue;
            var prompt = Prompts.Render("agent_actions", new Dictionary<string, string>
                { ["file"] = DocSelection.ForModel(p), ["text"] = Retriever.Sanitize(text.Text), ["today"] = Clock.Today() });
            var extracted = await ctx.AskModelAsync(prompt, "extract_tasks", ModelOutput.ParseExtract, ct).ConfigureAwait(false);
            var normText = DocSelection.Norm(text.Text);
            var injection = InjectionHeuristics.Check(text.Text);
            if (injection.Count > 0 && extracted.Tasks.Count > 0)
                ctx.Log("warning", $"Action items from “{p}” need your review: the document contains text aimed at an AI ({string.Join(", ", injection)}).");
            foreach (var t in extracted.Tasks.Take(6))
            {
                var title = t.Title.Replace('\n', ' ').Trim().TrimEnd('.');
                if (title.Length == 0 || title.Length > TaskStore.MaxTitleLength || !seen.Add(DocSelection.Norm(title))) continue;
                var priority = TaskPriority.IsValid(t.Priority.ToLowerInvariant()) ? t.Priority.ToLowerInvariant() : TaskPriority.Medium;
                var due = Clock.IsValidDate(t.DueDate.Trim()) ? t.DueDate.Trim() : null;
                var quote = t.Evidence.Trim().Trim('"', '“', '”');
                actions.Add(new JsonObject
                {
                    ["title"] = title, ["description"] = t.Description.Trim(), ["priority"] = priority, ["due_date"] = due,
                    ["source"] = p, ["quote"] = quote, ["quote_verified"] = quote.Length > 0 && normText.Contains(DocSelection.Norm(quote)),
                    ["suspicious_source"] = injection.Count > 0,
                });
            }
        }
        ctx.Artifacts["actions"] = actions;
        if (actions.Count == 0 && errors.Count == paths.Count) return ToolOutcome.Failure("No documents could be read: " + string.Join(" ", errors.Take(3)));
        var verified = actions.Count(a => a!["quote_verified"]!.GetValue<bool>());
        return ToolOutcome.Success(actions.Count == 0 ? "No action items found." :
            $"Found {actions.Count} action item(s); {verified} with a quote confirmed in the source.",
            new JsonObject { ["actions"] = actions.Count, ["quotes_verified"] = verified });
    }

    public IReadOnlyList<VerificationCheck> Verify(ToolContext ctx, JsonObject args, ToolOutcome outcome)
    {
        var actions = ctx.ArtifactArray("actions").OfType<JsonNode>().ToList();
        var unverified = actions.Count(a => !a["quote_verified"]!.GetValue<bool>());
        return
        [
            new("Action items were extracted from real documents", outcome.Ok, outcome.Summary),
            // Informational: unverified quotes are flagged to the user, not treated as a failure.
            new("Supporting quotes found in the source", true, unverified == 0 ? "All quotes confirmed" : $"{unverified} item(s) flagged: quote not found in the document"),
        ];
    }
}

/// <summary>tasks.create — turns approved action items into SlothBoss tasks (deduplicated by title).</summary>
public sealed class CreateTasksTool : IAgentTool
{
    public ToolSpec Spec { get; } = new(
        "tasks.create", 1, "Create tasks in SlothBoss from the action items found earlier.",
        [], "tasks_created: [{id, title, updated_at}]",
        ToolRisk.Create, ToolScope.Workspace, TimeSpan.FromSeconds(30), 1, Idempotent: true, UsesModel: false,
        "Each created task is read back from the database.", ["actions"], ["tasks_created"]);

    private static bool Suspicious(JsonNode a) => a["suspicious_source"]?.GetValue<bool>() == true;

    public IReadOnlyList<ApprovalItem> ApprovalItems(ToolContext ctx) =>
        ctx.ArtifactArray("actions").OfType<JsonNode>().Select((a, i) => new ApprovalItem(i,
            $"Create task “{a["title"]!.GetValue<string>()}” ({a["priority"]!.GetValue<string>()}{(a["due_date"]?.GetValue<string>() is { } d ? ", due " + d : "")})",
            Suspicious(a)
                ? $"From {a["source"]!.GetValue<string>()} — this document contains instructions aimed at an AI, so this item may not be a real to-do; check before approving"
                : $"From {a["source"]!.GetValue<string>()}" + (a["quote_verified"]!.GetValue<bool>() ? $": “{a["quote"]!.GetValue<string>()}”" : " — quote not found in the document; check before approving"),
            Suggested: !Suspicious(a)))
        .ToList();

    /// <summary>
    /// Items whose quote couldn't be found in the source, or that come from a document trying to instruct
    /// an AI, are never created without a person looking.
    /// </summary>
    public bool RequiresReview(ToolContext ctx) =>
        ctx.ArtifactArray("actions").OfType<JsonNode>().Any(a => !a["quote_verified"]!.GetValue<bool>() || Suspicious(a));

    public Task<ToolOutcome> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var created = ctx.ArtifactArray("tasks_created");
        var existing = ctx.Ws.Tasks.List(new TaskFilter(HideDone: true)).Select(t => DocSelection.Norm(t.Title)).ToHashSet();
        int made = 0, skipped = 0;
        foreach (var (a, i) in ctx.ArtifactArray("actions").OfType<JsonNode>().Select((a, i) => (a, i)).ToList())
        {
            ct.ThrowIfCancellationRequested();
            ctx.CheckStop();
            if (ctx.Step.Excluded.Contains(i)) { skipped++; continue; }
            var title = a["title"]!.GetValue<string>();
            if (!existing.Add(DocSelection.Norm(title))) { skipped++; ctx.Log("info", $"“{title}” already exists as a task; not duplicated."); continue; }
            var task = ctx.Ws.Tasks.Create(new TaskDraft(title,
                $"{a["description"]!.GetValue<string>()}\nFrom: {a["source"]!.GetValue<string>()}".Trim(),
                Priority: a["priority"]!.GetValue<string>(), DueDate: a["due_date"]?.GetValue<string>()));
            created.Add(new JsonObject { ["id"] = task.Id, ["title"] = task.Title, ["updated_at"] = task.UpdatedAt });
            made++;
            ctx.PersistProgress();
        }
        ctx.Artifacts["tasks_created"] = created;
        return Task.FromResult(ToolOutcome.Success($"Created {made} task(s)" + (skipped > 0 ? $", skipped {skipped}" : "") + ".",
            new JsonObject { ["created"] = made, ["skipped"] = skipped }));
    }

    public IReadOnlyList<VerificationCheck> Verify(ToolContext ctx, JsonObject args, ToolOutcome outcome)
    {
        var created = ctx.ArtifactArray("tasks_created").OfType<JsonNode>().ToList();
        var missing = created.Count(c => ctx.Ws.Tasks.Get(c["id"]!.GetValue<string>()) is null);
        return [new("Created tasks are in the database", missing == 0, missing == 0 ? $"{created.Count} task(s) read back" : $"{missing} task(s) missing")];
    }

    public IReadOnlyList<string> Undo(ToolContext ctx)
    {
        var notes = new List<string>();
        foreach (var c in ctx.ArtifactArray("tasks_created").OfType<JsonNode>())
        {
            var t = ctx.Ws.Tasks.Get(c["id"]!.GetValue<string>());
            if (t is null) continue;
            if (t.UpdatedAt != c["updated_at"]!.GetValue<string>()) { notes.Add($"Task “{t.Title}” was edited, so it was kept."); continue; }
            ctx.Ws.Tasks.Delete(t.Id);
        }
        return notes;
    }
}

/// <summary>notes.search — finds related SlothBoss pages and tasks.</summary>
public sealed class SearchNotesTool : IAgentTool
{
    public ToolSpec Spec { get; } = new(
        "notes.search", 1, "Search the user's SlothBoss pages and tasks for related notes.",
        [new ToolArg("query", "string", "Words to search for", null)],
        "notes: [{kind, id, title, snippet}]",
        ToolRisk.Read, ToolScope.Workspace, TimeSpan.FromSeconds(15), 1, Idempotent: true, UsesModel: false,
        "Every hit refers to an existing page or task.", [], ["notes"]);

    public Task<ToolOutcome> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var query = ToolArgs.Str(args, "query");
        var terms = query.Length > 0 ? SearchService.Tokenize(query) : Retriever.Keywords(ctx.Run.Goal);
        var hits = ctx.Ws.Search.QueryAny(terms, 8);
        ctx.Artifacts["notes"] = new JsonArray([.. hits.Select(h => (JsonNode)new JsonObject
        {
            ["kind"] = h.Kind, ["id"] = h.RefId, ["title"] = h.Title,
            ["snippet"] = h.Snippet.Replace(SearchHit.MatchStart.ToString(), "").Replace(SearchHit.MatchEnd.ToString(), ""),
        })]);
        return Task.FromResult(ToolOutcome.Success(hits.Count == 0 ? "No related notes found." : $"Found {hits.Count} related note(s).",
            new JsonObject { ["hits"] = hits.Count }));
    }

    public IReadOnlyList<VerificationCheck> Verify(ToolContext ctx, JsonObject args, ToolOutcome outcome)
    {
        var notes = ctx.ArtifactArray("notes").OfType<JsonNode>().ToList();
        var missing = notes.Count(n => n["kind"]!.GetValue<string>() == "page" ? !ctx.Ws.Pages.Exists(n["id"]!.GetValue<string>()) : ctx.Ws.Tasks.Get(n["id"]!.GetValue<string>()) is null);
        return [new("Search hits refer to existing records", missing == 0, $"{notes.Count} hit(s)")];
    }
}

/// <summary>report.create — writes the run's results as a SlothBoss page, built only from recorded results.</summary>
public sealed class CreateReportTool : IAgentTool
{
    public ToolSpec Spec { get; } = new(
        "report.create", 1, "Write a report page in SlothBoss with what was found and done (optionally also a Markdown copy in the folder).",
        [new ToolArg("save_copy_in_folder", "boolean", "Also save the report as a .md file in the approved folder", false)],
        "report: {page_id, file}",
        ToolRisk.Create, ToolScope.Workspace, TimeSpan.FromSeconds(30), 1, Idempotent: false, UsesModel: false,
        "The page is read back from the database; the file copy is read back from disk.", [], ["report"]);

    public static string TitleFor(RunRecord run) =>
        $"SlothBoss report: {(run.Goal.Length > 60 ? run.Goal[..57].TrimEnd() + "…" : run.Goal)} [#{run.Id[..6]}]";

    public Task<ToolOutcome> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var title = TitleFor(ctx.Run);
        var md = BuildMarkdown(ctx);
        // Re-running (after "retry failed steps") refreshes the run's own report page — unless the user edited it.
        var previous = ctx.Artifacts["report"] as JsonObject;
        if (previous?["page_id"]?.GetValue<string>() is { } existingId && ctx.Ws.Pages.Get(existingId) is { } existing
            && existing.UpdatedAt == previous["page_updated_at"]?.GetValue<string>())
        {
            var updatedAt = ctx.Ws.Pages.UpdateContent(existingId, Markdown.Parse(md));
            previous["page_updated_at"] = updatedAt;
            return Task.FromResult(ToolOutcome.Success($"Updated the report page “{title}” with the latest results" +
                (previous["file"] is null ? "." : $"; the earlier copy {previous["file"]} was left unchanged (files are never overwritten)."), (JsonObject)previous.DeepClone()));
        }
        var page = ctx.Ws.Pages.Create(title, null, Markdown.Parse(md));
        var report = new JsonObject { ["page_id"] = page.Id, ["page_updated_at"] = page.UpdatedAt };
        if (ToolArgs.Bool(args, "save_copy_in_folder") && ctx.MaybeSandbox is { } sb)
        {
            var rel = sb.UniqueTarget($"SlothBoss report {DateTime.Now:yyyy-MM-dd HHmm}.md", new HashSet<string>());
            var op = ctx.Ws.Agent.AddFileOp(ctx.Run.Id, ctx.Step.Id, "create_file", null, rel, null, null, "Report copy");
            ctx.Ws.Agent.UpdateOp(op.Id, "running");
            sb.CreateNewFile(rel, $"# {title}\n\n{md}\n");
            var sha = sb.HashRel(rel);
            ctx.Ws.Agent.SetOpHash(op.Id, sha);
            ctx.Ws.Agent.UpdateOp(op.Id, "applied");
            report["file"] = rel;
            report["file_sha"] = sha;
        }
        ctx.Artifacts["report"] = report;
        return Task.FromResult(ToolOutcome.Success($"Wrote the report page “{title}”" + (report["file"] is null ? "." : $" and {report["file"]}."), report));
    }

    private static string BuildMarkdown(ToolContext ctx)
    {
        var a = ctx.Artifacts;
        var sb = new StringBuilder();
        sb.Append("**Goal:** ").Append(Markdown.EscapeText(ctx.Run.Goal)).Append("\n\n");
        if (ctx.Run.FolderPath is not null) sb.Append("**Folder:** ").Append(Markdown.EscapeText(ctx.Run.FolderPath)).Append("\n\n");
        sb.Append("Built by SlothBoss from the run's recorded results. Final verification is shown in the run view.\n\n");

        var ops = ctx.Ws.Agent.FileOps(ctx.Run.Id).Where(o => o.Op == "move").ToList();
        if (ops.Count > 0)
        {
            sb.Append("## File changes\n");
            foreach (var o in ops.Where(o => o.Status == "applied")) sb.Append("- Moved ").Append(Markdown.EscapeText(o.SourceRel!)).Append(" → ").Append(Markdown.EscapeText(o.TargetRel)).Append('\n');
            foreach (var o in ops.Where(o => o.Status is "excluded")) sb.Append("- Skipped at your request: ").Append(Markdown.EscapeText(o.SourceRel!)).Append('\n');
            foreach (var o in ops.Where(o => o.Status is "failed")) sb.Append("- Not moved: ").Append(Markdown.EscapeText(o.SourceRel!)).Append(" (").Append(Markdown.EscapeText(o.Error ?? "")).Append(")\n");
            foreach (var o in ops.Where(o => o.Status is "proposed")) sb.Append("- Proposed, not applied: ").Append(Markdown.EscapeText(o.SourceRel!)).Append(" → ").Append(Markdown.EscapeText(o.TargetRel)).Append('\n');
            sb.Append('\n');
        }
        if (a["duplicates"] is JsonArray dups && dups.Count > 0)
        {
            sb.Append("## Identical files\n");
            foreach (var g in dups) sb.Append("- ").Append(Markdown.EscapeText(string.Join(" = ", g!["files"]!.AsArray().Select(f => f!.GetValue<string>())))).Append('\n');
            sb.Append('\n');
        }
        if (a["summaries"] is JsonArray sums && sums.Count > 0)
        {
            sb.Append("## Document summaries\n");
            foreach (var s in sums)
            {
                sb.Append("### ").Append(Markdown.EscapeText(s!["path"]!.GetValue<string>())).Append('\n');
                sb.Append(Markdown.EscapeText(s["summary"]!.GetValue<string>())).Append('\n');
                foreach (var k in s["key_points"]!.AsArray()) sb.Append("- ").Append(Markdown.EscapeText(k!.GetValue<string>())).Append('\n');
                sb.Append('\n');
            }
        }
        if (a["actions"] is JsonArray acts && acts.Count > 0)
        {
            sb.Append("## Action items found\n");
            foreach (var x in acts)
                sb.Append("- [ ] ").Append(Markdown.EscapeText(x!["title"]!.GetValue<string>()))
                  .Append(x["due_date"]?.GetValue<string>() is { } d ? $" (due {d})" : "")
                  .Append(" — from ").Append(Markdown.EscapeText(x["source"]!.GetValue<string>()))
                  .Append(x["quote_verified"]!.GetValue<bool>() ? "" : " (quote not found in the document)").Append('\n');
            sb.Append('\n');
        }
        if (a["tasks_created"] is JsonArray tc && tc.Count > 0) sb.Append($"{tc.Count} task(s) were added to SlothBoss.\n\n");
        var notDone = ctx.Ws.Agent.Steps(ctx.Run.Id).Where(s => s.Status is StepStatus.Failed or StepStatus.Skipped or StepStatus.Rejected).ToList();
        if (notDone.Count > 0)
        {
            sb.Append("## Not done\n");
            foreach (var s in notDone) sb.Append("- ").Append(s.Tool).Append(": ").Append(Markdown.EscapeText(s.Error ?? s.Status.Name())).Append('\n');
        }
        return sb.ToString();
    }

    public IReadOnlyList<VerificationCheck> Verify(ToolContext ctx, JsonObject args, ToolOutcome outcome)
    {
        var r = ctx.Artifacts["report"];
        var page = r?["page_id"]?.GetValue<string>() is { } id ? ctx.Ws.Pages.Get(id) : null;
        var checks = new List<VerificationCheck> { new("Report page saved and readable", page is not null && page.ContentText.Contains("Goal"), page is null ? "Page not found" : page.DisplayTitle) };
        if (r?["file"]?.GetValue<string>() is { } file)
        {
            var ok = ctx.Sandbox.FileExists(file) && ctx.Sandbox.HashRel(file) == r["file_sha"]!.GetValue<string>();
            checks.Add(new("Report copy saved in the folder", ok, file));
        }
        return checks;
    }

    public Reconciliation Reconcile(ToolContext ctx)
    {
        var title = TitleFor(ctx.Run);
        var existing = ctx.Ws.Pages.List(includeArchived: true).FirstOrDefault(p => p.Title == title);
        if (existing is null) return new(ReconcileResult.NotDone, "No report page was created before the interruption.");
        var page = ctx.Ws.Pages.Get(existing.Id)!;
        ctx.Artifacts["report"] = new JsonObject { ["page_id"] = page.Id, ["page_updated_at"] = page.UpdatedAt };
        return new(ReconcileResult.Done, "The report page had already been created.");
    }

    public IReadOnlyList<string> Undo(ToolContext ctx)
    {
        var notes = new List<string>();
        var r = ctx.Artifacts["report"];
        if (r?["page_id"]?.GetValue<string>() is { } id && ctx.Ws.Pages.Get(id) is { } page)
        {
            if (page.UpdatedAt == r["page_updated_at"]!.GetValue<string>()) ctx.Ws.Pages.Delete(id);
            else notes.Add("The report page was edited, so it was kept.");
        }
        foreach (var o in ctx.Ws.Agent.FileOpsForStep(ctx.Step.Id).Where(o => o.Op == "create_file" && o.Status == "applied"))
        {
            // Only the report file SlothBoss itself created, and only if unchanged.
            if (ctx.MaybeSandbox is { } sb && sb.FileExists(o.TargetRel) && sb.HashRel(o.TargetRel) == o.Sha)
            {
                File.Delete(sb.Resolve(o.TargetRel));
                ctx.Ws.Agent.UpdateOp(o.Id, "undone");
            }
            else
            {
                ctx.Ws.Agent.UpdateOp(o.Id, "undo_failed", error: "The report copy was changed or removed, so it was left alone.");
                notes.Add("The report copy in the folder was changed, so it was kept.");
            }
        }
        return notes;
    }
}

public static class ToolRegistry
{
    /// <summary>The application's tools. Web tools are registered only when a web configuration is supplied,
    /// and even then stay hidden from the planner until the user enables web research and adds keys.</summary>
    public static IReadOnlyList<IAgentTool> Default(WebConfig? web = null) =>
    [
        new ListFilesTool(), new FindDuplicatesTool(), new PlanOrganizationTool(), new ApplyOrganizationTool(),
        new SummarizeDocsTool(), new ExtractActionsTool(), new CreateTasksTool(), new SearchNotesTool(), new CreateReportTool(),
        .. web is null ? Array.Empty<IAgentTool>() : [new WebSearchTool(web), new WebReadTool(web), new ResearchReportTool()],
    ];
}
