using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlothBoss.Core.Documents;

public enum BlockType { Paragraph, Heading, Bulleted, Numbered, Todo, Quote, Code }

public sealed class Span
{
    public string Text { get; set; } = "";
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Code { get; set; }
    public string? Link { get; set; }

    public Span() { }
    public Span(string text) => Text = text;

    public bool SameFormat(Span other) =>
        Bold == other.Bold && Italic == other.Italic && Code == other.Code && Link == other.Link;

    public Span Clone() => new() { Text = Text, Bold = Bold, Italic = Italic, Code = Code, Link = Link };
}

public sealed class Block
{
    public BlockType Type { get; set; } = BlockType.Paragraph;
    /// <summary>1-3, headings only.</summary>
    public int Level { get; set; }
    /// <summary>Todo only.</summary>
    public bool Checked { get; set; }
    public List<Span> Spans { get; set; } = [];

    public Block() { }
    public Block(BlockType type, params Span[] spans) { Type = type; Spans = [.. spans]; if (type == BlockType.Heading) Level = 1; }

    public string Text => string.Concat(Spans.Select(s => s.Text));

    public static Block Paragraph(string text) => new(BlockType.Paragraph, new Span(text));
    public static Block Heading(int level, string text) => new(BlockType.Heading, new Span(text)) { Level = level };
}

public sealed class DocumentFormatException(string message) : SlothBossException(message);

/// <summary>Structured page content, persisted as pages.content_json (contracts/schemas/content.schema.json).</summary>
public sealed class PageDocument
{
    public const int CurrentVersion = 1;
    public const string EmptyJson = "{\"version\":1,\"blocks\":[]}";

    public List<Block> Blocks { get; set; } = [];

    public PageDocument() { }
    public PageDocument(IEnumerable<Block> blocks) => Blocks = [.. blocks];

    private static readonly Dictionary<BlockType, string> TypeNames = new()
    {
        [BlockType.Paragraph] = "paragraph", [BlockType.Heading] = "heading", [BlockType.Bulleted] = "bulleted",
        [BlockType.Numbered] = "numbered", [BlockType.Todo] = "todo", [BlockType.Quote] = "quote", [BlockType.Code] = "code",
    };
    private static readonly Dictionary<string, BlockType> TypesByName = TypeNames.ToDictionary(kv => kv.Value, kv => kv.Key);

    public static string TypeName(BlockType t) => TypeNames[t];

    /// <summary>Merges adjacent equally formatted spans, drops empty spans and unsafe links, clamps heading levels.</summary>
    public PageDocument Normalize()
    {
        foreach (var block in Blocks)
        {
            if (block.Type == BlockType.Heading) block.Level = Math.Clamp(block.Level, 1, 3);
            else block.Level = 0;
            if (block.Type != BlockType.Todo) block.Checked = false;

            var merged = new List<Span>();
            foreach (var span in block.Spans)
            {
                if (span.Link is not null && !Links.IsAllowed(span.Link)) span.Link = null;
                if (block.Type == BlockType.Code) { span.Bold = span.Italic = span.Code = false; span.Link = null; }
                else span.Text = span.Text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
                if (span.Text.Length == 0) continue;
                if (merged.Count > 0 && merged[^1].SameFormat(span)) merged[^1].Text += span.Text;
                else merged.Add(span);
            }
            block.Spans = merged;
        }
        return this;
    }

    // ---- canonical JSON -------------------------------------------------------------------

    /// <summary>Canonical serialization: fixed key order, optional keys omitted when false/absent.</summary>
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", CurrentVersion);
            w.WriteStartArray("blocks");
            foreach (var b in Blocks)
            {
                w.WriteStartObject();
                w.WriteString("type", TypeNames[b.Type]);
                if (b.Type == BlockType.Heading) w.WriteNumber("level", b.Level);
                if (b.Type == BlockType.Todo) w.WriteBoolean("checked", b.Checked);
                w.WriteStartArray("spans");
                foreach (var s in b.Spans)
                {
                    w.WriteStartObject();
                    w.WriteString("text", s.Text);
                    if (s.Bold) w.WriteBoolean("bold", true);
                    if (s.Italic) w.WriteBoolean("italic", true);
                    if (s.Code) w.WriteBoolean("code", true);
                    if (s.Link is not null) w.WriteString("link", s.Link);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public JsonNode ToJsonNode() => JsonNode.Parse(ToJson())!;

    public static PageDocument Parse(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException e) { throw new DocumentFormatException($"Content is not valid JSON: {e.Message}"); }
        return FromJsonNode(node);
    }

    /// <summary>Strict parse: unknown keys, types or versions are rejected.</summary>
    public static PageDocument FromJsonNode(JsonNode? node)
    {
        if (node is not JsonObject root) throw new DocumentFormatException("Content must be a JSON object.");
        RejectUnknownKeys(root, "content", "version", "blocks");
        if (root["version"] is not JsonValue v || !v.TryGetValue<int>(out var version))
            throw new DocumentFormatException("Content is missing an integer 'version'.");
        if (version != CurrentVersion)
            throw new DocumentFormatException($"Unsupported content version {version}; this build supports version {CurrentVersion}.");
        if (root["blocks"] is not JsonArray blocks) throw new DocumentFormatException("Content is missing 'blocks'.");

        var doc = new PageDocument();
        foreach (var item in blocks)
        {
            if (item is not JsonObject bo) throw new DocumentFormatException("Each block must be an object.");
            RejectUnknownKeys(bo, "block", "type", "level", "checked", "spans");
            var typeName = GetString(bo, "type") ?? throw new DocumentFormatException("Block is missing 'type'.");
            if (!TypesByName.TryGetValue(typeName, out var type)) throw new DocumentFormatException($"Unknown block type '{typeName}'.");
            var block = new Block { Type = type };
            if (type == BlockType.Heading)
            {
                if (bo["level"] is not JsonValue lv || !lv.TryGetValue<int>(out var level) || level is < 1 or > 3)
                    throw new DocumentFormatException("Heading level must be 1, 2 or 3.");
                block.Level = level;
            }
            if (type == BlockType.Todo) block.Checked = GetBool(bo, "checked");
            if (bo["spans"] is not JsonArray spans) throw new DocumentFormatException("Block is missing 'spans'.");
            foreach (var sitem in spans)
            {
                if (sitem is not JsonObject so) throw new DocumentFormatException("Each span must be an object.");
                RejectUnknownKeys(so, "span", "text", "bold", "italic", "code", "link");
                var text = GetString(so, "text") ?? throw new DocumentFormatException("Span is missing 'text'.");
                block.Spans.Add(new Span
                {
                    Text = text, Bold = GetBool(so, "bold"), Italic = GetBool(so, "italic"),
                    Code = GetBool(so, "code"), Link = GetString(so, "link"),
                });
            }
            doc.Blocks.Add(block);
        }
        return doc.Normalize();
    }

    private static void RejectUnknownKeys(JsonObject o, string what, params string[] allowed)
    {
        foreach (var key in o.Select(kv => kv.Key))
            if (!allowed.Contains(key)) throw new DocumentFormatException($"Unknown {what} property '{key}'.");
    }

    private static string? GetString(JsonObject o, string key) =>
        o[key] switch
        {
            null => null,
            JsonValue jv when jv.TryGetValue<string>(out var s) => s,
            _ => throw new DocumentFormatException($"Property '{key}' must be a string."),
        };

    private static bool GetBool(JsonObject o, string key) =>
        o[key] switch
        {
            null => false,
            JsonValue jv when jv.TryGetValue<bool>(out var b) => b,
            _ => throw new DocumentFormatException($"Property '{key}' must be a boolean."),
        };

    // ---- projections ------------------------------------------------------------------------

    /// <summary>Plain-text projection stored in pages.content_text: one line per block.</summary>
    public string ToPlainText() => string.Join("\n", Blocks.Select(b => b.Text).Where(t => t.Length > 0));

    /// <summary>Distinct page ids referenced via slothboss://page/ links.</summary>
    public IReadOnlyList<string> PageReferences() =>
        Blocks.SelectMany(b => b.Spans)
              .Select(s => Links.TryParsePageLink(s.Link))
              .OfType<string>()
              .Distinct()
              .ToList();
}

public static class Links
{
    public const string PagePrefix = "slothboss://page/";

    public static string ForPage(string pageId) => PagePrefix + pageId;

    public static string? TryParsePageLink(string? url)
    {
        if (url is null || !url.StartsWith(PagePrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var id = url[PagePrefix.Length..].ToLowerInvariant();
        return Ids.IsValid(id) ? id : null;
    }

    /// <summary>Only web, mail and internal page links are kept; javascript:, file: and others are dropped.</summary>
    public static bool IsAllowed(string url)
    {
        if (TryParsePageLink(url) is not null) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme is "http" or "https" or "mailto";
    }
}
