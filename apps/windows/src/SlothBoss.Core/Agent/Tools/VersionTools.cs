using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SlothBoss.Core.Ai;

namespace SlothBoss.Core.Agent.Tools;

/// <summary>
/// files.compare_versions — finds several versions of the same file ("paper.docx", "paper (1).docx",
/// "paper_final.docx", "Emission factors.xlsx" / "Emission_factors_corrected.xlsx"), says which one is the
/// newest, which copies are identical to it, and what changed in the others (lines added/removed, with
/// examples). Deterministic: no model call, nothing changes on disk.
/// </summary>
public sealed partial class CompareVersionsTool : IAgentTool
{
    public const string OlderVersionsFolder = "Older versions";
    private const int MaxGroups = 15, MaxFilesPerGroup = 8, MaxExtractions = 30, MaxExamples = 3;

    public ToolSpec Spec { get; } = new(
        "files.compare_versions", 1,
        "Find different versions of the same file (like report.docx, report (1).docx, report_final.docx), tell which one is the newest, which copies are identical, and what changed between them.",
        [], "versions: [{name, newest, files: [{path, modified, same_as_newest, added, removed, added_examples, removed_examples}]}]",
        ToolRisk.Read, ToolScope.ApprovedFolder, TimeSpan.FromMinutes(5), 1, Idempotent: true, UsesModel: false,
        "Checks that every compared file still exists.", ["inventory"], ["versions"]);

    // Version markers at the end of a name: "(2)", "- Copy", "_final", "corrected", "v3", "updated", …
    // Copy numbers may touch the name ("README(4)"); words need a separator ("renew" is not "re" + "new").
    [GeneratedRegex(@"([\s_\-.]*(\(\d+\)|\[\d+\])|[\s_\-.]+(copy|final|corrected|updated|revised|edited|latest|new|old|draft|v\d+))+$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionSuffix();

    [GeneratedRegex(@"[\s_\-.]+")]
    private static partial Regex Separators();

    /// <summary>"Emission_factors_corrected" and "Emission factors (1)" → "emission factors".</summary>
    public static string BaseName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName).Trim();
        var stripped = VersionSuffix().Replace(stem, "");
        if (stripped.Length < 3) stripped = stem; // "final.docx" stays "final"
        return Separators().Replace(stripped, " ").Trim().ToLowerInvariant();
    }

    public Task<ToolOutcome> ExecuteAsync(ToolContext ctx, JsonObject args, CancellationToken ct)
    {
        var inv = ctx.ArtifactArray("inventory").OfType<JsonObject>()
            .Where(n => !Inventory.Path(n).StartsWith(PlanOrganizationTool.DuplicatesFolder + "\\", StringComparison.OrdinalIgnoreCase)
                     && !Inventory.Path(n).StartsWith(OlderVersionsFolder + "\\", StringComparison.OrdinalIgnoreCase));
        var groups = inv.GroupBy(n => (BaseName(n["name"]!.GetValue<string>()), Inventory.Ext(n).ToLowerInvariant()))
            .Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Count()).Take(MaxGroups).ToList();

        var extractions = 0;
        var result = new JsonArray();
        foreach (var g in groups)
        {
            ct.ThrowIfCancellationRequested();
            ctx.CheckStop();
            // Newest = most recently modified; ties broken by the higher copy number / longer name.
            var files = g.OrderByDescending(n => n["modified"]!.GetValue<string>(), StringComparer.Ordinal)
                         .ThenByDescending(n => Inventory.Path(n).Length).Take(MaxFilesPerGroup).ToList();
            var newest = files[0];
            var newestPath = Inventory.Path(newest);
            var newestSha = Sha(ctx, newest);
            var readable = DocumentText.IsSupported(Inventory.Ext(newest)) && !DocumentText.IsImage(Inventory.Ext(newest));
            HashSet<string>? newestUnits = null;
            if (readable && extractions < MaxExtractions) { newestUnits = Units(ctx, newestPath); extractions++; }

            var entries = new JsonArray { Entry(newest, true, null) };
            foreach (var f in files.Skip(1))
            {
                var same = newestSha is not null && Sha(ctx, f) == newestSha;
                JsonObject? diff = null;
                if (!same && newestUnits is not null && extractions < MaxExtractions && Units(ctx, Inventory.Path(f)) is { } units)
                {
                    extractions++;
                    var added = newestUnits.Where(u => !units.Contains(u)).ToList();
                    var removed = units.Where(u => !newestUnits.Contains(u)).ToList();
                    diff = new JsonObject
                    {
                        ["added"] = added.Count, ["removed"] = removed.Count,
                        ["added_examples"] = Examples(added), ["removed_examples"] = Examples(removed),
                    };
                }
                entries.Add(Entry(f, same, diff));
            }
            result.Add(new JsonObject
            {
                ["name"] = g.Key.Item1, ["ext"] = g.Key.Item2, ["newest"] = newestPath, ["files"] = entries,
            });
        }
        ctx.Artifacts["versions"] = result;

        if (result.Count == 0) return Task.FromResult(ToolOutcome.Success("No files with several versions were found.", new JsonObject { ["groups"] = 0 }));
        var lines = result.Take(4).Select(v =>
        {
            var fs = v!["files"]!.AsArray();
            var identical = fs.Skip(1).Count(f => f!["same_as_newest"]!.GetValue<bool>());
            var different = fs.Count - 1 - identical;
            return $"“{Path.GetFileName(v["newest"]!.GetValue<string>())}” is the newest of {fs.Count}" +
                   (identical > 0 ? $"; {identical} identical" : "") + (different > 0 ? $"; {different} different" : "");
        });
        return Task.FromResult(ToolOutcome.Success(
            $"Found {result.Count} file(s) with several versions. " + string.Join(". ", lines) + (result.Count > 4 ? ". …" : "."),
            new JsonObject { ["groups"] = result.Count, ["files"] = result.Sum(v => v!["files"]!.AsArray().Count) }));
    }

    private static JsonObject Entry(JsonObject n, bool sameAsNewest, JsonObject? diff)
    {
        var o = new JsonObject
        {
            ["path"] = Inventory.Path(n), ["modified"] = n["modified"]!.GetValue<string>(), ["size"] = n["size"]!.GetValue<long>(),
            ["same_as_newest"] = sameAsNewest,
        };
        if (diff is not null) foreach (var (k, v) in diff) o[k] = v?.DeepClone();
        return o;
    }

    private static string? Sha(ToolContext ctx, JsonObject n)
    {
        if (Inventory.Sha(n) is { } known) return known;
        if (n["size"]!.GetValue<long>() > 200L * 1024 * 1024) return null;
        try
        {
            var sha = ctx.Sandbox.HashRel(Inventory.Path(n));
            n["sha"] = sha;
            return sha;
        }
        catch (IOException) { return null; }
    }

    /// <summary>The document's text split into comparable units (lines, and sentences of long lines).</summary>
    private static HashSet<string>? Units(ToolContext ctx, string relPath)
    {
        try
        {
            var text = DocumentText.Extract(ctx.Sandbox.Resolve(relPath), 60000).Text;
            return Regex.Split(text, @"\n|(?<=[.!?])\s+(?=[A-Z0-9])")
                .Select(u => Regex.Replace(u, @"\s+", " ").Trim())
                .Where(u => u.Length >= 3).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception e) when (e is SlothBossException or IOException) { return null; }
    }

    private static JsonArray Examples(List<string> units) =>
        new([.. units.OrderByDescending(u => u.Length).Take(MaxExamples)
            .Select(u => (JsonNode)Retriever.Sanitize(u.Length > 140 ? u[..140] + "…" : u))]);

    public IReadOnlyList<VerificationCheck> Verify(ToolContext ctx, JsonObject args, ToolOutcome outcome)
    {
        var paths = ctx.ArtifactArray("versions").OfType<JsonNode>().SelectMany(v => v["files"]!.AsArray().Select(f => f!["path"]!.GetValue<string>())).ToList();
        var missing = paths.Count(p => !File.Exists(ctx.Sandbox.Resolve(p)));
        return [new("Compared files exist", missing == 0, missing == 0 ? $"{paths.Count} file(s) compared" : $"{missing} file(s) vanished")];
    }
}
