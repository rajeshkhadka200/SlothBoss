using System.Text;
using System.Text.RegularExpressions;

namespace SlothBoss.Core.Documents;

/// <summary>
/// Markdown conversion. Export rules are specified in docs/DATA_MODEL.md and pinned by
/// contracts/fixtures/content; the macOS client implements the same rules.
/// </summary>
public static partial class Markdown
{
    private static bool IsListType(BlockType t) => t is BlockType.Bulleted or BlockType.Numbered or BlockType.Todo;

    public static string ExportPage(string title, PageDocument doc)
    {
        var body = Export(doc);
        var heading = "# " + EscapeText(string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim());
        return body.Length == 0 ? heading + "\n" : heading + "\n\n" + body + "\n";
    }

    public static string Export(PageDocument doc)
    {
        var sb = new StringBuilder();
        Block? prev = null;
        var number = 0;
        foreach (var block in doc.Blocks)
        {
            if (block.Text.Trim().Length == 0 && block.Type != BlockType.Code) { prev = null; number = 0; continue; }
            if (prev is not null) sb.Append(IsListType(prev.Type) && IsListType(block.Type) ? "\n" : "\n\n");
            number = block.Type == BlockType.Numbered ? number + 1 : 0;
            switch (block.Type)
            {
                case BlockType.Heading: sb.Append(new string('#', block.Level)).Append(' ').Append(Inline(block.Spans)); break;
                case BlockType.Bulleted: sb.Append("- ").Append(Inline(block.Spans)); break;
                case BlockType.Numbered: sb.Append(number).Append(". ").Append(Inline(block.Spans)); break;
                case BlockType.Todo: sb.Append(block.Checked ? "- [x] " : "- [ ] ").Append(Inline(block.Spans)); break;
                case BlockType.Quote: sb.Append("> ").Append(Inline(block.Spans)); break;
                case BlockType.Code: sb.Append("```\n").Append(block.Text).Append("\n```"); break;
                default: sb.Append(Inline(block.Spans)); break;
            }
            prev = block;
        }
        return sb.ToString();
    }

    private static string Inline(List<Span> spans)
    {
        var sb = new StringBuilder();
        foreach (var span in spans)
        {
            var text = span.Text;
            var core = text.Trim();
            if (core.Length == 0) { sb.Append(text); continue; }
            var lead = text[..text.IndexOf(core[0])];
            var trail = text[(lead.Length + core.Length)..];

            var rendered = span.Code ? "`" + core.Replace("`", "'") + "`" : EscapeText(core);
            if (span.Bold && span.Italic) rendered = "***" + rendered + "***";
            else if (span.Bold) rendered = "**" + rendered + "**";
            else if (span.Italic) rendered = "*" + rendered + "*";
            if (span.Link is not null) rendered = "[" + rendered + "](" + span.Link.Replace(")", "%29").Replace(" ", "%20") + ")";
            sb.Append(lead).Append(rendered).Append(trail);
        }
        return sb.ToString();
    }

    public static string EscapeText(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '\\' or '*' or '_' or '`' or '[' or ']') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ---- parsing (used for AI drafted notes) ------------------------------------------------

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")] private static partial Regex HeadingRx();
    [GeneratedRegex(@"^[-*+]\s+\[([ xX])\]\s+(.*)$")] private static partial Regex TodoRx();
    [GeneratedRegex(@"^[-*+]\s+(.*)$")] private static partial Regex BulletRx();
    [GeneratedRegex(@"^\d{1,9}[.)]\s+(.*)$")] private static partial Regex NumberRx();
    [GeneratedRegex(@"^>\s?(.*)$")] private static partial Regex QuoteRx();

    public static PageDocument Parse(string markdown)
    {
        var doc = new PageDocument();
        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            doc.Blocks.Add(new Block { Type = BlockType.Paragraph, Spans = ParseInline(string.Join(" ", paragraph)) });
            paragraph.Clear();
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                var code = new List<string>();
                for (i++; i < lines.Length && !lines[i].Trim().StartsWith("```", StringComparison.Ordinal); i++) code.Add(lines[i]);
                doc.Blocks.Add(new Block { Type = BlockType.Code, Spans = [new Span(string.Join("\n", code))] });
                continue;
            }
            if (line.Length == 0) { FlushParagraph(); continue; }

            Match m;
            if ((m = HeadingRx().Match(line)).Success)
            {
                FlushParagraph();
                doc.Blocks.Add(new Block { Type = BlockType.Heading, Level = Math.Min(m.Groups[1].Length, 3), Spans = ParseInline(m.Groups[2].Value.TrimEnd('#', ' ')) });
            }
            else if ((m = TodoRx().Match(line)).Success)
            {
                FlushParagraph();
                doc.Blocks.Add(new Block { Type = BlockType.Todo, Checked = m.Groups[1].Value is "x" or "X", Spans = ParseInline(m.Groups[2].Value) });
            }
            else if ((m = BulletRx().Match(line)).Success)
            {
                FlushParagraph();
                doc.Blocks.Add(new Block { Type = BlockType.Bulleted, Spans = ParseInline(m.Groups[1].Value) });
            }
            else if ((m = NumberRx().Match(line)).Success)
            {
                FlushParagraph();
                doc.Blocks.Add(new Block { Type = BlockType.Numbered, Spans = ParseInline(m.Groups[1].Value) });
            }
            else if ((m = QuoteRx().Match(line)).Success)
            {
                FlushParagraph();
                doc.Blocks.Add(new Block { Type = BlockType.Quote, Spans = ParseInline(m.Groups[1].Value) });
            }
            else paragraph.Add(line);
        }
        FlushParagraph();
        return doc.Normalize();
    }

    /// <summary>Parses **bold**, *italic* / _italic_, `code`, [text](url) and backslash escapes. No nesting beyond bold+italic.</summary>
    public static List<Span> ParseInline(string s)
    {
        var spans = new List<Span>();
        var text = new StringBuilder();
        bool bold = false, italic = false;

        void Flush(string? link = null)
        {
            if (text.Length == 0) return;
            spans.Add(new Span { Text = text.ToString(), Bold = bold, Italic = italic, Link = link });
            text.Clear();
        }

        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length && "\\*_`[]()#>-".Contains(s[i + 1])) { text.Append(s[++i]); continue; }
            if (c == '`')
            {
                var end = s.IndexOf('`', i + 1);
                if (end > i)
                {
                    Flush();
                    spans.Add(new Span { Text = s[(i + 1)..end], Code = true, Bold = bold, Italic = italic });
                    i = end;
                    continue;
                }
            }
            if (c == '[')
            {
                var close = FindClosingBracket(s, i);
                if (close > i && close + 1 < s.Length && s[close + 1] == '(')
                {
                    var paren = s.IndexOf(')', close + 2);
                    if (paren > close)
                    {
                        Flush();
                        var url = s[(close + 2)..paren].Trim();
                        var inner = ParseInline(s[(i + 1)..close]);
                        var link = Links.IsAllowed(url) ? url : null;
                        foreach (var sp in inner)
                        {
                            sp.Bold |= bold; sp.Italic |= italic; sp.Link = link;
                            spans.Add(sp);
                        }
                        i = paren;
                        continue;
                    }
                }
            }
            if (c == '*' || c == '_')
            {
                var run = 1;
                while (i + run < s.Length && s[i + run] == c && run < 3) run++;
                // An underscore inside a word (snake_case) is literal.
                if (c == '_' && i > 0 && char.IsLetterOrDigit(s[i - 1]) && i + run < s.Length && char.IsLetterOrDigit(s[i + run]))
                { text.Append(s, i, run); i += run - 1; continue; }
                Flush();
                if (run >= 2) bold = !bold;
                if (run is 1 or 3) italic = !italic;
                i += run - 1;
                continue;
            }
            text.Append(c);
        }
        Flush();
        return spans;
    }

    private static int FindClosingBracket(string s, int open)
    {
        var depth = 0;
        for (var j = open; j < s.Length; j++)
        {
            if (s[j] == '\\') { j++; continue; }
            if (s[j] == '[') depth++;
            else if (s[j] == ']' && --depth == 0) return j;
        }
        return -1;
    }
}
