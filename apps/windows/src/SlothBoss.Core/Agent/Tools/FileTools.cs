using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SlothBoss.Core.Ai;

namespace SlothBoss.Core.Agent.Tools;

internal static class Inventory
{
    public static JsonObject ToJson(FileEntry e, string? sha = null)
    {
        var o = new JsonObject
        {
            ["path"] = e.RelPath, ["name"] = e.Name, ["ext"] = e.Extension, ["size"] = e.Size, ["modified"] = e.ModifiedAt, ["depth"] = e.Depth,
        };
        if (sha is not null) o["sha"] = sha;
        return o;
    }

    public static string Path(JsonNode n) => n["path"]!.GetValue<string>();
    public static string Ext(JsonNode n) => n["ext"]!.GetValue<string>();
    public static int Depth(JsonNode n) => n["depth"]!.GetValue<int>();
    public static string? Sha(JsonNode n) => n["sha"]?.GetValue<string>();

    public static IEnumerable<JsonNode> Documents(JsonArray inventory) =>
        inventory.OfType<JsonNode>().Where(n => DocumentText.IsSupported(Ext(n)));

    /// <summary>
    /// After a file moved, rewrites every recorded reference to it (inventory, duplicates, summaries,
    /// action sources) so later steps and the final verification look where the file is now.
    /// </summary>
    public static void Relocate(JsonObject artifacts, string from, string to)
    {
        static bool Same(JsonNode? n, string p) => n is JsonValue v && v.TryGetValue<string>(out var s) && string.Equals(s, p, StringComparison.OrdinalIgnoreCase);
        foreach (var n in (artifacts["inventory"] as JsonArray ?? []).OfType<JsonObject>().Where(n => Same(n["path"], from)))
        {
            n["path"] = to;
            n["name"] = System.IO.Path.GetFileName(to);
            n["depth"] = to.Count(c => c == '\\');
        }
        foreach (var g in (artifacts["duplicates"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Same(g["keep"], from)) g["keep"] = to;
            var files = g["files"]!.AsArray();
            for (var i = 0; i < files.Count; i++) if (Same(files[i], from)) files[i] = to;
        }
        foreach (var v in (artifacts["versions"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Same(v["newest"], from)) v["newest"] = to;
            foreach (var f in v["files"]!.AsArray().OfType<JsonObject>().Where(f => Same(f["path"], from))) f["path"] = to;
        }
        foreach (var s in (artifacts["summaries"] as JsonArray ?? []).OfType<JsonObject>().Where(s => Same(s["path"], from))) s["path"] = to;
        foreach (var a in (artifacts["actions"] as JsonArray ?? []).OfType<JsonObject>().Where(a => Same(a["source"], from))) a["source"] = to;
    }
}

/// <summary>files.list — inventory of the approved folder.</summary>
public sealed class ListFilesTool : IAgentTool
{
    public ToolSpec Spec { get; } = new(
        "files.list", 1, "List the files in the approved folder (names, types, sizes).",
        [new ToolArg("max_depth", "integer", "How many folder levels to look into", 2, Min: 1, Max: 3)],
        "inventory: [{path, name, ext, size, modified, depth}]",
        ToolRisk.Read, ToolScope.ApprovedFolder, TimeSpan.FromSeconds(60), 1, Idempotent: true, UsesModel: false,
        "Re-checks that listed files exist.", [], ["inventory"]);

    public Task<ToolOutcome> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var files = ctx.Sandbox.List(ToolArgs.Int(args, "max_depth", 2), ctx.Budget.MaxFiles, out var truncated);
        ctx.Artifacts["inventory"] = new JsonArray([.. files.Select(f => (JsonNode)Inventory.ToJson(f))]);
        var byType = files.GroupBy(f => f.Extension.Length == 0 ? "(none)" : f.Extension).OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} {g.Key}").Take(6);
        return Task.FromResult(ToolOutcome.Success(
            $"Found {files.Count} file(s){(truncated ? $" (stopped at the {ctx.Budget.MaxFiles}-file limit)" : "")}: {string.Join(", ", byType)}.",
            new JsonObject { ["count"] = files.Count, ["truncated"] = truncated }));
    }

    public IReadOnlyList<VerificationCheck> Verify(ToolContext ctx, JsonObject args, ToolOutcome outcome)
    {
        var inv = ctx.ArtifactArray("inventory");
        var missing = inv.OfType<JsonNode>().Take(50).Count(n => !File.Exists(ctx.Sandbox.Resolve(Inventory.Path(n))));
        return [new("Listed files exist on disk", missing == 0, missing == 0 ? $"{inv.Count} file(s) listed" : $"{missing} listed file(s) vanished")];
    }
}

/// <summary>files.find_duplicates — groups byte-identical files by SHA-256.</summary>
public sealed class FindDuplicatesTool : IAgentTool
{
    public ToolSpec Spec { get; } = new(
        "files.find_duplicates", 1, "Find files with identical content (SHA-256), even if their names differ.",
        [], "duplicates: [{sha, size, files[], keep}]",
        ToolRisk.Read, ToolScope.ApprovedFolder, TimeSpan.FromMinutes(5), 1, Idempotent: true, UsesModel: false,
        "Re-hashes the first two files of each group.", ["inventory"], ["duplicates"]);

    public Task<ToolOutcome> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var inv = ctx.ArtifactArray("inventory");
        long hashed = 0;
        var skipped = 0;
        // Only files sharing a size can be duplicates; hash just those.
        foreach (var group in inv.OfType<JsonObject>().GroupBy(n => n["size"]!.GetValue<long>()).Where(g => g.Count() > 1 && g.Key > 0))
            foreach (var n in group)
            {
                ct.ThrowIfCancellationRequested();
                ctx.CheckStop();
                if (hashed + group.Key > ctx.Budget.MaxBytesHashed) { skipped++; continue; }
                try { n["sha"] = ctx.Sandbox.HashRel(Inventory.Path(n)); hashed += group.Key; }
                catch (IOException) { skipped++; }
            }

        var groups = inv.OfType<JsonObject>().Where(n => Inventory.Sha(n) is not null).GroupBy(n => Inventory.Sha(n)!).Where(g => g.Count() > 1)
            .Select(g =>
            {
                // Keep the oldest copy (usually the original), then the shortest name.
                var ordered = g.OrderBy(n => n["modified"]!.GetValue<string>(), StringComparer.Ordinal).ThenBy(n => Inventory.Path(n).Length).ToList();
                return new JsonObject
                {
                    ["sha"] = g.Key, ["size"] = g.First()["size"]!.GetValue<long>(),
                    ["keep"] = Inventory.Path(ordered[0]),
                    ["files"] = new JsonArray([.. ordered.Select(n => (JsonNode)Inventory.Path(n))]),
                };
            }).ToList();
        ctx.Artifacts["duplicates"] = new JsonArray([.. groups]);
        var extra = groups.Sum(g => g["files"]!.AsArray().Count - 1);
        return Task.FromResult(ToolOutcome.Success(
            groups.Count == 0 ? "No duplicate files found." : $"Found {groups.Count} group(s) of identical files ({extra} extra copies)." +
            (skipped > 0 ? $" {skipped} file(s) could not be hashed." : ""),
            new JsonObject { ["groups"] = groups.Count, ["extra_copies"] = extra, ["skipped"] = skipped }));
    }

    public IReadOnlyList<VerificationCheck> Verify(ToolContext ctx, JsonObject args, ToolOutcome outcome)
    {
        var bad = 0;
        foreach (var g in ctx.ArtifactArray("duplicates").OfType<JsonNode>())
        {
            var files = g["files"]!.AsArray().Select(f => f!.GetValue<string>()).Take(2).ToList();
            var sha = g["sha"]!.GetValue<string>();
            if (files.Any(f => !File.Exists(ctx.Sandbox.Resolve(f)) || ctx.Sandbox.HashRel(f) != sha)) bad++;
        }
        return [new("Duplicate groups re-hashed and identical", bad == 0, bad == 0 ? "All groups confirmed" : $"{bad} group(s) changed")];
    }
}

/// <summary>
/// files.plan_organization — computes a concrete, reviewable list of folder creations and moves.
/// Writes only the proposal; nothing on disk changes.
/// </summary>
public sealed partial class PlanOrganizationTool : IAgentTool
{
    public const string DuplicatesFolder = "Duplicates";

    public ToolSpec Spec { get; } = new(
        "files.plan_organization", 1,
        "Propose how to organize the top level of the folder into subfolders (by file type, or by topic using document contents), move extra duplicate copies into a Duplicates folder (and older versions into Older versions, after files.compare_versions), and optionally suggest clearer names for unclear document names. Changes nothing yet.",
        [
            new ToolArg("strategy", "string", "by_type groups by kind of file; by_topic asks the model to group documents by subject", "by_type", ["by_type", "by_topic"]),
            new ToolArg("rename_unclear", "boolean", "Suggest clearer names for documents named like scan001 or document (2)", false),
        ],
        "proposal: {moves, folders, renames, duplicates}; file operations stored for review",
        ToolRisk.Read, ToolScope.ApprovedFolder, TimeSpan.FromMinutes(5), 1, Idempotent: true, UsesModel: true,
        "Checks every source still exists and every target is free, inside the folder and unique.", ["inventory"], ["proposal"]);

    private static readonly Dictionary<string, string> TypeFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "Documents", [".docx"] = "Documents", [".doc"] = "Documents", [".txt"] = "Documents", [".md"] = "Documents",
        [".rtf"] = "Documents", [".odt"] = "Documents", [".pptx"] = "Presentations", [".ppt"] = "Presentations",
        [".csv"] = "Spreadsheets", [".xlsx"] = "Spreadsheets", [".xls"] = "Spreadsheets", [".ods"] = "Spreadsheets",
        [".jpg"] = "Images", [".jpeg"] = "Images", [".png"] = "Images", [".gif"] = "Images", [".heic"] = "Images", [".webp"] = "Images", [".bmp"] = "Images", [".svg"] = "Images",
        [".zip"] = "Archives", [".7z"] = "Archives", [".rar"] = "Archives", [".tar"] = "Archives", [".gz"] = "Archives",
        [".exe"] = "Installers", [".msi"] = "Installers", [".dmg"] = "Installers", [".pkg"] = "Installers",
        [".mp3"] = "Audio", [".wav"] = "Audio", [".m4a"] = "Audio", [".flac"] = "Audio",
        [".mp4"] = "Video", [".mov"] = "Video", [".mkv"] = "Video", [".avi"] = "Video",
        [".json"] = "Data", [".jsonl"] = "Data", [".xml"] = "Data", [".log"] = "Data", [".yaml"] = "Data", [".yml"] = "Data",
        [".html"] = "Web pages", [".htm"] = "Web pages",
        [".py"] = "Code", [".ipynb"] = "Code", [".js"] = "Code", [".ts"] = "Code", [".java"] = "Code", [".c"] = "Code", [".cpp"] = "Code", [".cs"] = "Code", [".sh"] = "Code", [".ps1"] = "Code",
        [".tmp"] = "Temporary files",
    };

    [GeneratedRegex(@"^(scan|img|image|doc|document|download|untitled|file|new|copy|dsc|screenshot|unnamed)?[\s_\-]*\(?\d*\)?[\s_\-]*\d*$", RegexOptions.IgnoreCase)]
    private static partial Regex UnclearStem();

    // Camera/phone/app-generated names: "WhatsApp Image 2025-01-02 at 10.11.12", "ChatGPT Image Mar 3, 2026, 09_41_10 PM",
    // "Screenshot 2026-05-01 101112", "IMG_20250101_123456", "PXL_…", or a bare GUID.
    [GeneratedRegex(@"^((whatsapp|chatgpt|gemini) image|screenshot|screen shot|img|pxl|dsc|photo|image)([\s_\-.,:]|\d|\bat\b|\b[ap]m\b|\b(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\b|\(\d+\))*$|^(chatgpt|gemini|copilot|dall[\s\-]?e|midjourney)\b.*\d{4}|^[0-9a-f]{8}(-?[0-9a-f]{4}){3}-?[0-9a-f]{12}$", RegexOptions.IgnoreCase)]
    private static partial Regex GeneratedStem();

    public const int MaxImagesLookedAt = 6;

    public static bool IsUnclearName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).Trim();
        return stem.Length <= 3 || UnclearStem().IsMatch(stem) || GeneratedStem().IsMatch(stem);
    }

    public async Task<ToolOutcome> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var sb = ctx.Sandbox;
        var strategy = ToolArgs.Str(args, "strategy");
        var renameUnclear = ToolArgs.Bool(args, "rename_unclear");
        var inv = ctx.ArtifactArray("inventory");
        var top = inv.OfType<JsonObject>().Where(n => Inventory.Depth(n) == 0).ToList();

        // 1. Duplicates: extra copies go to Duplicates/, the kept copy is organized normally.
        var duplicateOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in ctx.ArtifactArray("duplicates").OfType<JsonNode>())
        {
            var keep = g["keep"]!.GetValue<string>();
            foreach (var f in g["files"]!.AsArray().Select(x => x!.GetValue<string>()).Where(f => !string.Equals(f, keep, StringComparison.OrdinalIgnoreCase)))
                duplicateOf[f] = keep;
        }
        // Older versions (when versions were compared): set aside next to the newest, which is organized normally.
        var olderThan = new Dictionary<string, (string Newest, JsonNode Entry)>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in ctx.ArtifactArray("versions").OfType<JsonNode>())
        {
            var newest = v["newest"]!.GetValue<string>();
            foreach (var f in v["files"]!.AsArray().OfType<JsonNode>().Where(f => !string.Equals(f["path"]!.GetValue<string>(), newest, StringComparison.OrdinalIgnoreCase)))
                if (!duplicateOf.ContainsKey(f["path"]!.GetValue<string>())) olderThan[f["path"]!.GetValue<string>()] = (newest, f);
        }

        // 2. Model suggestions (topic folders and/or clearer names) for readable documents only.
        var suggestions = new Dictionary<string, (string? Folder, string? NewName)>(StringComparer.OrdinalIgnoreCase);
        var candidates = top.Where(n => DocumentText.IsSupported(Inventory.Ext(n)) && !duplicateOf.ContainsKey(Inventory.Path(n)) && !olderThan.ContainsKey(Inventory.Path(n)))
            .Where(n => strategy == "by_topic" || renameUnclear && IsUnclearName(n["name"]!.GetValue<string>()))
            .ToList();
        // Looking at an image costs a vision-model call, so only a few are looked at (unclear names first);
        // the rest simply go to Images/ by type.
        var images = candidates.Where(n => DocumentText.IsImage(Inventory.Ext(n)))
            .OrderBy(n => IsUnclearName(n["name"]!.GetValue<string>()) ? 0 : 1).Take(MaxImagesLookedAt).ToList();
        var docs = candidates.Where(n => !DocumentText.IsImage(Inventory.Ext(n))).Take(20).Concat(images).ToList();
        var modelUsed = false;
        var sbDocs = new System.Text.StringBuilder();
        if (docs.Count > 0)
        {
            var imageErrors = new List<string>();
            foreach (var d in docs.ToList())
            {
                ctx.CheckStop();
                var path = Inventory.Path(d);
                string snippet;
                if (DocumentText.IsImage(Inventory.Ext(d)))
                {
                    var described = await ContentReader.DescribeImageAsync(ctx, path, imageErrors, ct).ConfigureAwait(false);
                    if (described is null) { docs.Remove(d); continue; }
                    snippet = "(image) " + described;
                }
                else
                {
                    try { snippet = DocumentText.Extract(sb.Resolve(path), 400).Text; }
                    catch (Exception e) when (e is SlothBossException or IOException) { snippet = "(unreadable)"; }
                }
                sbDocs.Append("file: ").Append(DocSelection.ForModel(path)).Append('\n').Append(Retriever.Sanitize(snippet.Replace("\n", " "))).Append("\n\n");
            }
            if (imageErrors.Count > 0) ctx.Log("warning", $"Images not looked at: {imageErrors[0]}{(imageErrors.Count > 1 ? $" (+{imageErrors.Count - 1} more)" : "")}");
        }
        if (docs.Count > 0)
        {
            var prompt = Prompts.Render("agent_organize", new Dictionary<string, string> { ["documents"] = sbDocs.ToString(), ["max_folders"] = "6" });
            var assignments = await ctx.AskModelAsync(prompt, "agent_organize", ParseAssignments, ct).ConfigureAwait(false);
            modelUsed = true;
            var allowedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (file, folder, newName) in assignments)
            {
                var matchedPath = DocSelection.MatchFile(docs.Select(Inventory.Path).ToList(), file);
                var match = matchedPath is null ? null : docs.First(d => Inventory.Path(d) == matchedPath);
                if (match is null) { ctx.Log("warning", $"Ignored a suggestion for a file that isn't in the folder: “{Trim(file)}”."); continue; }
                var safeFolder = strategy == "by_topic" ? FileSandbox.SafeSegment(folder, 40) : null;
                if (safeFolder is not null && !allowedFolders.Contains(safeFolder))
                {
                    if (allowedFolders.Count >= 6) safeFolder = null; else allowedFolders.Add(safeFolder);
                }
                var safeName = renameUnclear && IsUnclearName(match["name"]!.GetValue<string>()) ? FileSandbox.SafeSegment(newName, 70) : null;
                suggestions[Inventory.Path(match)] = (safeFolder, safeName);
            }
        }

        // 3. Build concrete operations.
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var moves = new List<(string Source, string Target, string Sha, long Size, string Reason, string? Note)>();
        foreach (var n in top)
        {
            ctx.CheckStop();
            var path = Inventory.Path(n);
            var name = n["name"]!.GetValue<string>();
            string folder, fileName = name, reason;
            if (duplicateOf.TryGetValue(path, out var keep))
            {
                folder = DuplicatesFolder;
                reason = $"Identical to “{keep}” (same SHA-256); extra copy set aside, not deleted";
            }
            else if (olderThan.TryGetValue(path, out var older))
            {
                folder = CompareVersionsTool.OlderVersionsFolder;
                var e = older.Entry;
                reason = $"Older version of “{Path.GetFileName(older.Newest)}” (newest, modified later)" +
                         (e["same_as_newest"]!.GetValue<bool>() ? "; identical content" :
                          e["added"] is { } add ? $"; the newest has {add} new and {e["removed"]} removed line(s)" : "") + "; set aside, not deleted";
            }
            else
            {
                suggestions.TryGetValue(path, out var s);
                if (s.Folder is not null) { folder = s.Folder; reason = $"Topic “{s.Folder}” suggested by the local model from the document's text"; }
                else if (TypeFolders.TryGetValue(Inventory.Ext(n), out var typeFolder)) { folder = typeFolder; reason = $"{Inventory.Ext(n).TrimStart('.').ToUpperInvariant()} file → {typeFolder}"; }
                else { folder = "Other"; reason = "Unrecognized file type → Other"; }
                if (s.NewName is not null && !string.Equals(s.NewName, Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase))
                {
                    fileName = s.NewName + Path.GetExtension(name);
                    reason += $"; renamed from unclear name “{name}” (suggested by the local model)";
                }
            }
            var wanted = Path.Combine(folder, fileName);
            var target = sb.UniqueTarget(wanted, claimed);
            string? note = target.Equals(wanted, StringComparison.OrdinalIgnoreCase) ? null : $"“{wanted}” is taken, so it will be named “{Path.GetFileName(target)}”";
            var sha = Inventory.Sha(n) ?? sb.HashRel(path);
            moves.Add((path, target, sha, n["size"]!.GetValue<long>(), reason, note));
        }

        var folders = moves.Select(m => Path.GetDirectoryName(m.Target)!).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f => !Directory.Exists(sb.Resolve(f))).ToList();
        var ops = folders.Select(f => ("create_folder", (string?)null, f, (string?)null, (long?)null, "Folder for organized files", (string?)null))
            .Concat(moves.Select(m => ("move", (string?)m.Source, m.Target, (string?)m.Sha, (long?)m.Size, m.Reason, m.Note)))
            .ToList();
        ctx.Ws.Agent.ReplaceProposal(ctx.Run.Id, ctx.Step.Id, ops);

        var renames = moves.Count(m => !Path.GetFileName(m.Target).Equals(Path.GetFileName(m.Source), StringComparison.OrdinalIgnoreCase) && m.Note is null);
        ctx.Artifacts["proposal"] = new JsonObject
        {
            ["strategy"] = strategy, ["moves"] = moves.Count, ["folders"] = folders.Count, ["renames"] = renames,
            ["duplicates"] = moves.Count(m => m.Target.StartsWith(DuplicatesFolder + "\\", StringComparison.OrdinalIgnoreCase)),
            ["older_versions"] = moves.Count(m => m.Target.StartsWith(CompareVersionsTool.OlderVersionsFolder + "\\", StringComparison.OrdinalIgnoreCase)),
            ["collisions_resolved"] = moves.Count(m => m.Note is not null), ["model_used"] = modelUsed,
        };
        return ToolOutcome.Success(
            moves.Count == 0 ? "Nothing at the top level of the folder needs organizing." :
            $"Proposed {moves.Count} move(s) into {moves.Select(m => Path.GetDirectoryName(m.Target)).Distinct(StringComparer.OrdinalIgnoreCase).Count()} folder(s)" +
            (renames > 0 ? $", {renames} clearer name(s)" : "") + (folders.Count > 0 ? $", {folders.Count} new folder(s)" : "") + ". Nothing has changed yet.",
            (JsonObject)ctx.Artifacts["proposal"]!.DeepClone());
    }

    private static string Trim(string s) => s.Length > 60 ? s[..60] + "…" : s;

    internal static List<(string File, string Folder, string NewName)> ParseAssignments(string text)
    {
        var o = ModelOutput.ExtractJson(text);
        if (o["assignments"] is not JsonArray arr) throw new ModelOutputException("Missing 'assignments' list.");
        return arr.Select(a => a as JsonObject ?? throw new ModelOutputException("Each assignment must be an object."))
            .Select(a => (Get(a, "file"), Get(a, "folder"), Get(a, "new_name"))).ToList();

        static string Get(JsonObject a, string k) =>
            a[k] is null ? "" : a[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : throw new ModelOutputException($"'{k}' must be text.");
    }

    public IReadOnlyList<VerificationCheck> Verify(ToolContext ctx, JsonObject args, ToolOutcome outcome)
    {
        var ops = ctx.Ws.Agent.FileOps(ctx.Run.Id).Where(o => o.Status == "proposed").ToList();
        var missingSource = ops.Count(o => o.Op == "move" && !File.Exists(ctx.Sandbox.Resolve(o.SourceRel!)));
        var takenTarget = ops.Count(o => o.Op == "move" && File.Exists(ctx.Sandbox.Resolve(o.TargetRel)));
        var dupTargets = ops.Where(o => o.Op == "move").GroupBy(o => o.TargetRel.ToLowerInvariant()).Count(g => g.Count() > 1);
        return
        [
            new("Every file to move still exists", missingSource == 0, missingSource == 0 ? $"{ops.Count(o => o.Op == "move")} move(s) checked" : $"{missingSource} source file(s) missing"),
            new("No target would overwrite anything", takenTarget == 0 && dupTargets == 0, takenTarget + dupTargets == 0 ? "All targets free and unique" : $"{takenTarget} taken, {dupTargets} duplicated"),
        ];
    }
}

/// <summary>
/// files.apply_organization — performs the approved operations one by one. Each operation is journaled
/// as 'running' before it starts and checked against the file's recorded SHA-256 before and after, so a
/// retry or a resume after a crash never repeats or guesses.
/// </summary>
public sealed class ApplyOrganizationTool : IAgentTool
{
    public ToolSpec Spec { get; } = new(
        "files.apply_organization", 1,
        "Carry out the organization proposed by files.plan_organization (create folders, move files). Never deletes or overwrites; every change can be undone.",
        [], "applied: {moved, folders, failed, excluded}",
        ToolRisk.Modify, ToolScope.ApprovedFolder, TimeSpan.FromMinutes(10), 2, Idempotent: true, UsesModel: false,
        "Every moved file is found at its new path with the same SHA-256 and is gone from the old path.",
        ["proposal"], ["applied"], Replannable: false);

    public IReadOnlyList<ApprovalItem> ApprovalItems(ToolContext ctx) =>
        ctx.Ws.Agent.FileOps(ctx.Run.Id).Where(o => o.Status is "proposed" or "excluded" or "failed")
            .Select(o => new ApprovalItem(o.Seq,
                o.Op == "create_folder" ? $"Create folder “{o.TargetRel}”" : $"Move “{o.SourceRel}” → “{o.TargetRel}”",
                o.Note is null ? o.Reason : $"{o.Reason}. {o.Note}"))
            .ToList();

    public Task<ToolOutcome> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var sb = ctx.Sandbox;
        var store = ctx.Ws.Agent;
        store.AssignOpsToStep(ctx.Run.Id, ctx.Step.Id);
        var ops = store.FileOps(ctx.Run.Id).Where(o => o.StepId == ctx.Step.Id).ToList();
        foreach (var o in ops.Where(o => ctx.Step.Excluded.Contains(o.Seq) && o.Status == "proposed")) store.UpdateOp(o.Id, "excluded", "Excluded by the user");
        // Moves completed in an earlier attempt (or confirmed after a restart) must be reflected in later references too.
        foreach (var o in ops.Where(o => o.Op == "move" && o.Status == "applied")) Inventory.Relocate(ctx.Artifacts, o.SourceRel!, o.TargetRel);

        int moved = 0, folders = 0, failed = 0, transientFailures = 0;
        var errors = new List<string>();
        foreach (var o in store.FileOps(ctx.Run.Id).Where(o => o.StepId == ctx.Step.Id && o.Status is "proposed" or "failed" or "running"))
        {
            ct.ThrowIfCancellationRequested();
            ctx.CheckStop();
            ctx.CountFileOp();
            if (o.Op == "create_folder")
            {
                var full = sb.Resolve(o.TargetRel);
                var existed = Directory.Exists(full);
                store.UpdateOp(o.Id, "running");
                Directory.CreateDirectory(full);
                store.UpdateOp(o.Id, "applied", existed ? "existed" : "created");
                folders += existed ? 0 : 1;
                continue;
            }

            var src = o.SourceRel!;
            try
            {
                var srcFull = sb.Resolve(src);
                var dstFull = sb.Resolve(o.TargetRel);
                if (!File.Exists(srcFull))
                {
                    if (File.Exists(dstFull) && FileSandbox.Sha256(dstFull) == o.Sha)
                    {
                        store.UpdateOp(o.Id, "applied", "Already at the destination (verified by hash)");
                        Inventory.Relocate(ctx.Artifacts, src, o.TargetRel);
                        moved++;
                        continue;
                    }
                    throw new SlothBossException($"“{src}” is no longer there.");
                }
                if (FileSandbox.Sha256(srcFull) != o.Sha) throw new SlothBossException($"“{src}” changed after the plan was made, so it was left alone.");
                store.UpdateOp(o.Id, "running");
                sb.MoveNoOverwrite(src, o.TargetRel);
                if (!File.Exists(dstFull) || FileSandbox.Sha256(dstFull) != o.Sha) throw new SlothBossException($"After moving, “{o.TargetRel}” didn't match the original.");
                store.UpdateOp(o.Id, "applied");
                Inventory.Relocate(ctx.Artifacts, src, o.TargetRel);
                moved++;
            }
            catch (FileCollisionException e)
            {
                failed++;
                errors.Add(e.Message);
                store.UpdateOp(o.Id, "failed", error: e.Message);
            }
            catch (IOException e)
            {
                failed++;
                transientFailures++;
                var msg = $"“{src}” couldn't be moved ({e.Message.TrimEnd('.')}). It may be open in another program.";
                errors.Add(msg);
                store.UpdateOp(o.Id, "failed", error: msg);
            }
            catch (Exception e) when (e is SlothBossException or UnauthorizedAccessException)
            {
                failed++;
                errors.Add(e.Message);
                store.UpdateOp(o.Id, "failed", error: e.Message);
            }
        }

        var all = store.FileOps(ctx.Run.Id).Where(o => o.StepId == ctx.Step.Id).ToList();
        var data = new JsonObject
        {
            ["moved"] = all.Count(o => o.Op == "move" && o.Status == "applied"),
            ["folders"] = all.Count(o => o.Op == "create_folder" && o.Status == "applied" && o.Note == "created"),
            ["failed"] = all.Count(o => o.Status == "failed"),
            ["excluded"] = all.Count(o => o.Status == "excluded"),
        };
        ctx.Artifacts["applied"] = data.DeepClone();
        if (failed == 0)
            return Task.FromResult(ToolOutcome.Success($"Moved {data["moved"]} file(s), created {data["folders"]} folder(s)" +
                (data["excluded"]!.GetValue<int>() > 0 ? $"; {data["excluded"]} change(s) skipped as you asked" : "") + ".", data));
        var attempted = all.Count(o => o.Status != "excluded");
        return Task.FromResult(ToolOutcome.Failure(
            $"{all.Count(o => o.Status == "failed")} of {attempted} operation(s) failed: {string.Join(" ", errors.Take(3))}",
            transient: transientFailures == failed, data));
    }

    public IReadOnlyList<VerificationCheck> Verify(ToolContext ctx, JsonObject args, ToolOutcome outcome)
    {
        var ops = ctx.Ws.Agent.FileOps(ctx.Run.Id).Where(o => o.StepId == ctx.Step.Id).ToList();
        var applied = ops.Where(o => o.Status == "applied" && o.Op == "move").ToList();
        var bad = applied.Where(o => !File.Exists(ctx.Sandbox.Resolve(o.TargetRel)) || FileSandbox.Sha256(ctx.Sandbox.Resolve(o.TargetRel)) != o.Sha
                                     || File.Exists(ctx.Sandbox.Resolve(o.SourceRel!))).ToList();
        var pending = ops.Count(o => o.Status is "proposed" or "failed" or "running");
        return
        [
            new("Moved files are at their new paths with identical content", bad.Count == 0,
                bad.Count == 0 ? $"{applied.Count} file(s) verified by SHA-256" : $"{bad.Count} file(s) not where expected: {string.Join(", ", bad.Take(3).Select(b => b.TargetRel))}"),
            new("All approved operations were carried out", pending == 0, pending == 0 ? "None left over" : $"{pending} operation(s) not completed"),
        ];
    }

    public Reconciliation Reconcile(ToolContext ctx)
    {
        var store = ctx.Ws.Agent;
        var uncertain = new List<string>();
        foreach (var o in store.FileOps(ctx.Run.Id).Where(o => o.StepId == ctx.Step.Id && o.Status == "running"))
        {
            if (o.Op == "create_folder")
            {
                store.UpdateOp(o.Id, Directory.Exists(ctx.Sandbox.Resolve(o.TargetRel)) ? "applied" : "proposed", "Reconciled after restart");
                continue;
            }
            var src = ctx.Sandbox.Resolve(o.SourceRel!);
            var dst = ctx.Sandbox.Resolve(o.TargetRel);
            var atDst = File.Exists(dst) && FileSandbox.Sha256(dst) == o.Sha;
            var atSrc = File.Exists(src) && FileSandbox.Sha256(src) == o.Sha;
            if (atDst && !atSrc) store.UpdateOp(o.Id, "applied", "Confirmed after restart: the move had completed");
            else if (atSrc && !File.Exists(dst)) store.UpdateOp(o.Id, "proposed", "Confirmed after restart: the move had not happened");
            else uncertain.Add(o.SourceRel!);
        }
        return uncertain.Count == 0
            ? new(ReconcileResult.NotDone, "Interrupted moves were checked on disk; the rest can continue safely.")
            : new(ReconcileResult.Uncertain, $"Couldn't tell whether these moves finished: {string.Join(", ", uncertain)}. Check them, then resume.");
    }

    public IReadOnlyList<string> Undo(ToolContext ctx)
    {
        var notes = new List<string>();
        var store = ctx.Ws.Agent;
        foreach (var o in store.FileOps(ctx.Run.Id).Where(o => o.StepId == ctx.Step.Id && o.Status == "applied").OrderByDescending(o => o.Seq))
        {
            try
            {
                if (o.Op == "move")
                {
                    var dst = ctx.Sandbox.Resolve(o.TargetRel);
                    if (!File.Exists(dst)) throw new SlothBossException($"“{o.TargetRel}” is gone");
                    if (FileSandbox.Sha256(dst) != o.Sha) throw new SlothBossException($"“{o.TargetRel}” was edited after the move");
                    ctx.Sandbox.MoveNoOverwrite(o.TargetRel, o.SourceRel!);
                    store.UpdateOp(o.Id, "undone");
                }
                else if (o.Op == "create_folder")
                {
                    var dir = ctx.Sandbox.Resolve(o.TargetRel);
                    if (o.Note == "existed") { store.UpdateOp(o.Id, "undone", "Folder existed before; left in place"); continue; }
                    if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                    else if (Directory.Exists(dir)) throw new SlothBossException($"Folder “{o.TargetRel}” isn't empty, so it was kept");
                    store.UpdateOp(o.Id, "undone");
                }
            }
            catch (Exception e) when (e is SlothBossException or IOException or UnauthorizedAccessException)
            {
                store.UpdateOp(o.Id, "undo_failed", error: e.Message);
                notes.Add(e.Message + ".");
            }
        }
        return notes;
    }
}
