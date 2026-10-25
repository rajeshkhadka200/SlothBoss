using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SlothBoss.Core.Ai;

public sealed class ModelOutputException(string message) : SlothBossException(message);

public sealed record AskOutput(string Status, string Answer, IReadOnlyList<string> Citations, IReadOnlyList<string> Inferences);
public sealed record ExtractedTask(string Title, string Description, string Priority, string DueDate, string Evidence);
public sealed record ExtractOutput(IReadOnlyList<ExtractedTask> Tasks);
public sealed record PlannedTask(string Title, string Description, string Priority, string Source, string Rationale);
public sealed record PlanOutput(string Summary, IReadOnlyList<PlannedTask> Tasks);
public sealed record DraftOutput(string Title, string Markdown, IReadOnlyList<string> Citations);

/// <summary>
/// Parses and validates structured model responses. Constrained decoding (Ollama 'format') usually
/// produces valid JSON, but every response is still checked here; failures trigger a bounded retry.
/// </summary>
public static partial class ModelOutput
{
    public static readonly string[] AskStatuses = ["answered", "partial", "not_found"];

    public static JsonObject ExtractJson(string text)
    {
        var t = text.Trim();
        var start = t.IndexOf('{');
        var end = t.LastIndexOf('}');
        if (start < 0 || end <= start) throw new ModelOutputException("The reply did not contain a JSON object.");
        try
        {
            return JsonNode.Parse(t[start..(end + 1)]) as JsonObject ?? throw new ModelOutputException("The reply was not a JSON object.");
        }
        catch (JsonException e)
        {
            throw new ModelOutputException($"The reply was not valid JSON ({e.Message}).");
        }
    }

    private static string Str(JsonObject o, string key, bool required = true)
    {
        var node = o[key];
        if (node is null) return required ? throw new ModelOutputException($"Missing field '{key}'.") : "";
        if (node is JsonValue v && v.TryGetValue<string>(out var s)) return s.Trim();
        throw new ModelOutputException($"Field '{key}' must be a string.");
    }

    private static List<string> StrList(JsonObject o, string key)
    {
        if (o[key] is null) return [];
        if (o[key] is not JsonArray arr) throw new ModelOutputException($"Field '{key}' must be a list.");
        return arr.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : throw new ModelOutputException($"'{key}' must contain strings."))
                  .Where(s => s.Length > 0).ToList();
    }

    private static List<JsonObject> ObjList(JsonObject o, string key)
    {
        if (o[key] is not JsonArray arr) throw new ModelOutputException($"Field '{key}' must be a list.");
        return arr.Select(n => n as JsonObject ?? throw new ModelOutputException($"'{key}' must contain objects.")).ToList();
    }

    public static AskOutput ParseAsk(string text)
    {
        var o = ExtractJson(text);
        var status = Str(o, "status");
        if (!AskStatuses.Contains(status)) throw new ModelOutputException($"Unknown status '{status}'.");
        var answer = Str(o, "answer");
        if (answer.Length == 0) throw new ModelOutputException("The answer was empty.");
        return new AskOutput(status, answer, StrList(o, "citations"), StrList(o, "inferences"));
    }

    public static ExtractOutput ParseExtract(string text)
    {
        var o = ExtractJson(text);
        var tasks = ObjList(o, "tasks").Select(t => new ExtractedTask(
            Str(t, "title"), Str(t, "description", false), Str(t, "priority", false), Str(t, "due_date", false), Str(t, "evidence", false))).ToList();
        return new ExtractOutput(tasks);
    }

    public static PlanOutput ParsePlan(string text)
    {
        var o = ExtractJson(text);
        var tasks = ObjList(o, "tasks").Select(t => new PlannedTask(
            Str(t, "title"), Str(t, "description", false), Str(t, "priority", false), Str(t, "source", false), Str(t, "rationale", false))).ToList();
        return new PlanOutput(Str(o, "summary", false), tasks);
    }

    public static DraftOutput ParseDraft(string text)
    {
        var o = ExtractJson(text);
        var title = Str(o, "title");
        var markdown = Str(o, "markdown");
        if (title.Length == 0 || markdown.Length == 0) throw new ModelOutputException("The draft needs a title and a body.");
        return new DraftOutput(title, markdown, StrList(o, "citations"));
    }

    // ---- citations ------------------------------------------------------------------------

    // Source ids: S# for workspace excerpts, W# for web sources.
    [GeneratedRegex(@"\[\s*([SW]\d+(?:\s*[,;]\s*[SW]\d+)*)\s*\]", RegexOptions.IgnoreCase)] private static partial Regex MarkerRx();
    [GeneratedRegex(@"[ \t]+([.,;:!?])")] private static partial Regex SpaceBeforePunctRx();
    [GeneratedRegex(@"[ \t]{2,}")] private static partial Regex MultiSpaceRx();

    public sealed record CitationCheck(string Text, IReadOnlyList<string> ValidIds, int InvalidCount);

    /// <summary>
    /// Keeps only citation markers that refer to sources actually retrieved for this request.
    /// Invented ids are removed from the text and counted, so the UI can say so.
    /// </summary>
    public static CitationCheck ValidateCitations(string text, IEnumerable<string> declared, Evidence evidence)
    {
        var valid = new List<string>();
        var invalid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string Canon(string id) => id.Trim().ToUpperInvariant();

        var cleaned = MarkerRx().Replace(text, m =>
        {
            var ids = m.Groups[1].Value.Split([',', ';']).Select(Canon).ToList();
            var keep = new List<string>();
            foreach (var id in ids)
            {
                if (evidence.Find(id) is not null) { keep.Add(id); if (!valid.Contains(id)) valid.Add(id); }
                else invalid.Add(id);
            }
            return keep.Count == 0 ? "" : "[" + string.Join(", ", keep) + "]";
        });

        foreach (var raw in declared)
        {
            var id = Canon(raw.Trim('[', ']'));
            if (evidence.Find(id) is not null) { if (!valid.Contains(id)) valid.Add(id); }
            else invalid.Add(id);
        }

        cleaned = MultiSpaceRx().Replace(SpaceBeforePunctRx().Replace(cleaned, "$1"), " ").Trim();
        return new CitationCheck(cleaned, valid, invalid.Count);
    }
}
