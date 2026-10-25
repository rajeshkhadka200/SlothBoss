using System.Text.RegularExpressions;

namespace SlothBoss.Core.Storage;

public sealed record SearchHit(string Kind, string RefId, string Title, string Snippet, double Rank)
{
    /// <summary>Snippet match markers (wrap matched terms). UIs render the enclosed text emphasized.</summary>
    public const char MatchStart = '\u0002', MatchEnd = '\u0003';
    public bool IsPage => Kind == "page";
}

/// <summary>Local full-text search over the FTS5 index maintained by schema triggers.</summary>
public sealed partial class SearchService(Workspace ws)
{
    [GeneratedRegex(@"[\p{L}\p{N}]+")] private static partial Regex TokenRx();

    public static IReadOnlyList<string> Tokenize(string text) =>
        TokenRx().Matches(text).Select(m => m.Value.ToLowerInvariant()).Distinct().ToList();

    /// <summary>
    /// Builds a safe FTS5 MATCH expression: each token is quoted (so user input can't inject FTS
    /// syntax) and prefix-matched. Returns null when there is nothing to search for.
    /// </summary>
    public static string? BuildMatch(IEnumerable<string> tokens, bool any)
    {
        var parts = tokens.Where(t => t.Length > 0).Take(16).Select(t => "\"" + t.Replace("\"", "\"\"") + "\"*").ToList();
        return parts.Count == 0 ? null : string.Join(any ? " OR " : " ", parts);
    }

    /// <summary>All terms must match (prefix). Empty or symbol-only queries return no results.</summary>
    public List<SearchHit> Query(string query, int limit = 40) => Run(BuildMatch(Tokenize(query), any: false), limit);

    /// <summary>Any term may match; used for AI retrieval where recall matters more than precision.</summary>
    public List<SearchHit> QueryAny(IEnumerable<string> terms, int limit = 20) => Run(BuildMatch(terms, any: true), limit);

    private List<SearchHit> Run(string? match, int limit)
    {
        if (match is null) return [];
        return ws.Read(c => c.Query($"""
            SELECT s.kind, s.ref_id,
                   coalesce(p.title, t.title, ''),
                   snippet(search_index, 3, char(2), char(3), '…', 14),
                   bm25(search_index, 0.0, 0.0, 6.0, 1.0) AS rank
            FROM search_index s
            LEFT JOIN pages p ON s.kind = 'page' AND p.id = s.ref_id
            LEFT JOIN tasks t ON s.kind = 'task' AND t.id = s.ref_id
            WHERE search_index MATCH $q
            ORDER BY rank
            LIMIT $n
            """,
            r => new SearchHit(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDouble(4)),
            null, ("$q", match), ("$n", limit)));
    }
}
