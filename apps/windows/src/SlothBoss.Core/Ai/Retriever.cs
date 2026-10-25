using System.Text;
using System.Text.RegularExpressions;
using SlothBoss.Core.Storage;

namespace SlothBoss.Core.Ai;

/// <summary>One retrieved excerpt. <see cref="Id"/> (S1, S2, …) is the only identifier the model ever sees.</summary>
public sealed record Source(string Id, string Kind, string RefId, string Title, string Excerpt, string? PageId)
{
    public bool IsPage => Kind == "page";
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "Untitled" : Title;
}

public sealed record Evidence(IReadOnlyList<Source> Sources, bool Truncated)
{
    public bool IsEmpty => Sources.Count == 0;

    public Source? Find(string id) => Sources.FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Renders the evidence block placed inside &lt;evidence&gt; tags in prompts.</summary>
    public string Format()
    {
        var sb = new StringBuilder();
        foreach (var s in Sources)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append('[').Append(s.Id).Append("] ").Append(s.IsPage ? "Page" : "Task").Append(": \"")
              .Append(Retriever.Sanitize(s.DisplayTitle)).Append("\"\n").Append(Retriever.Sanitize(s.Excerpt));
        }
        return sb.ToString();
    }
}

/// <summary>
/// Full-text retrieval for grounded answers: keyword extraction → FTS5 OR query ranked by bm25 →
/// per-page chunk scoring → budgeted evidence list. Specified in docs/ARCHITECTURE.md#retrieval.
/// </summary>
public sealed partial class Retriever(Workspace ws)
{
    public int CharBudget { get; init; } = 6000;
    public int MaxSources { get; init; } = 8;
    public int ChunkChars { get; init; } = 700;

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a","an","the","and","or","but","if","then","else","of","to","in","on","at","by","for","with","about","as","into","from",
        "is","are","was","were","be","been","being","am","do","does","did","have","has","had","can","could","should","would",
        "will","shall","may","might","must","i","me","my","we","our","you","your","it","its","they","them","their","this","that",
        "these","those","what","which","who","whom","whose","when","where","why","how","there","here","any","all","some","each",
        "so","not","no","yes","just","also","than","too","very","up","out","over","again","please","tell","show","give","find",
        "list","summarize","explain","notes","note","workspace","pages","page","based","according","use","using","make","get",
    };

    [GeneratedRegex("</?\\s*evidence\\s*>", RegexOptions.IgnoreCase)] private static partial Regex EvidenceTagRx();

    /// <summary>Prevents stored content from closing the evidence block early (prompt-injection hygiene).</summary>
    public static string Sanitize(string text) => EvidenceTagRx().Replace(text, "(evidence)");

    public static IReadOnlyList<string> Keywords(string question) =>
        SearchService.Tokenize(question).Where(t => (t.Length >= 2 || char.IsDigit(t[0])) && !StopWords.Contains(t)).Take(12).ToList();

    public Evidence Retrieve(string question, string? pinnedPageId = null)
    {
        var keywords = Keywords(question);
        var sources = new List<(string Kind, string RefId, string Title, string Excerpt, string? PageId)>();
        var seenPages = new HashSet<string>();

        if (pinnedPageId is not null && ws.Pages.Get(pinnedPageId) is { } pinned)
        {
            seenPages.Add(pinned.Id);
            foreach (var chunk in BestChunks(pinned.ContentText, pinned.Title, keywords, max: 3, requireMatch: false))
                sources.Add(("page", pinned.Id, pinned.Title, chunk, pinned.Id));
        }

        if (keywords.Count > 0)
        {
            foreach (var hit in ws.Search.QueryAny(keywords, limit: 12))
            {
                if (hit.IsPage)
                {
                    if (!seenPages.Add(hit.RefId)) continue;
                    var page = ws.Pages.Get(hit.RefId);
                    if (page is null || page.IsArchived) continue;
                    foreach (var chunk in BestChunks(page.ContentText, page.Title, keywords, max: 2, requireMatch: true))
                        sources.Add(("page", page.Id, page.Title, chunk, page.Id));
                }
                else if (ws.Tasks.Get(hit.RefId) is { } task)
                {
                    var excerpt = new StringBuilder($"Status: {TaskState.Label(task.Status)}; priority: {task.Priority}");
                    if (task.DueDate is not null) excerpt.Append("; due ").Append(task.DueDate);
                    if (!string.IsNullOrWhiteSpace(task.Description)) excerpt.Append('\n').Append(Truncate(task.Description, ChunkChars));
                    sources.Add(("task", task.Id, task.Title, excerpt.ToString(), task.PageId));
                }
            }
        }

        var result = new List<Source>();
        var used = 0;
        var truncated = false;
        foreach (var s in sources)
        {
            if (result.Count >= MaxSources || used + s.Excerpt.Length > CharBudget) { truncated = true; continue; }
            used += s.Excerpt.Length;
            result.Add(new Source($"S{result.Count + 1}", s.Kind, s.RefId, s.Title, s.Excerpt, s.PageId));
        }
        return new Evidence(result, truncated);
    }

    /// <summary>The whole page as a single source, truncated to <paramref name="maxChars"/> (summaries, task extraction).</summary>
    public Evidence ForPage(string pageId, int maxChars = 12000)
    {
        var page = ws.Pages.Get(pageId) ?? throw new SlothBossException("The page no longer exists.");
        var text = page.ContentText;
        var truncated = text.Length > maxChars;
        if (truncated) text = text[..maxChars];
        return new Evidence([new Source("S1", "page", page.Id, page.Title, text, page.Id)], truncated);
    }

    internal IEnumerable<string> BestChunks(string text, string title, IReadOnlyList<string> keywords, int max, bool requireMatch)
    {
        var chunks = Chunk(text);
        if (chunks.Count == 0) yield break;
        var titleLower = title.ToLowerInvariant();
        var titleBonus = keywords.Any(k => titleLower.Contains(k)) ? 0.5 : 0;
        var scored = chunks.Select((c, i) => (Chunk: c, Index: i, Score: Score(c, keywords) + titleBonus)).ToList();
        var picked = scored.Where(s => s.Score > 0).OrderByDescending(s => s.Score).ThenBy(s => s.Index).Take(max).ToList();
        if (picked.Count == 0 && !requireMatch) picked = scored.Take(Math.Min(2, max)).ToList();
        foreach (var p in picked.OrderBy(p => p.Index)) yield return p.Chunk;
    }

    private static double Score(string chunk, IReadOnlyList<string> keywords)
    {
        var lower = chunk.ToLowerInvariant();
        double score = 0;
        foreach (var k in keywords)
        {
            var count = 0;
            for (var i = lower.IndexOf(k, StringComparison.Ordinal); i >= 0 && count < 5; i = lower.IndexOf(k, i + k.Length, StringComparison.Ordinal)) count++;
            if (count > 0) score += 1 + 0.25 * (count - 1);
        }
        return score;
    }

    internal List<string> Chunk(string text)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            foreach (var piece in SplitLong(line))
            {
                if (current.Length > 0 && current.Length + piece.Length + 1 > ChunkChars)
                {
                    chunks.Add(current.ToString());
                    current.Clear();
                }
                if (current.Length > 0) current.Append('\n');
                current.Append(piece);
            }
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }

    private IEnumerable<string> SplitLong(string line)
    {
        while (line.Length > ChunkChars)
        {
            var cut = line.LastIndexOfAny(['.', '!', '?', ';', ' '], ChunkChars - 1);
            if (cut < ChunkChars / 2) cut = ChunkChars - 1;
            yield return line[..(cut + 1)].Trim();
            line = line[(cut + 1)..].Trim();
        }
        if (line.Length > 0) yield return line;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
