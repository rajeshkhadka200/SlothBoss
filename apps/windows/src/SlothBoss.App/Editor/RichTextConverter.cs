using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using SlothBoss.Core.Documents;

namespace SlothBoss.App.Editor;

/// <summary>
/// Maps between the native RichEditBox (TOM text model) and the structured <see cref="PageDocument"/>.
/// Block semantics are encoded with formatting the user can't otherwise produce in SlothBoss:
///   heading 1/2/3 → bold at 28/22/18 pt; bulleted/numbered → paragraph list type;
///   quote → 18 pt left indent; code block → monospace paragraph with 12 pt left indent;
///   inline code → monospace run; checklist → "☐ " / "☑ " paragraph prefix; links → TOM hyperlinks.
/// Paste is forced to plain text so foreign formatting can't masquerade as structure.
/// </summary>
public static class RichTextConverter
{
    // TOM sizes are points (1 pt = 4/3 DIP): 11.25 pt = 15 px body, 21 pt = 28 px, 16.5 pt = 22 px, 13.5 pt = 18 px.
    public const float BodySize = 11.25f, H1Size = 21f, H2Size = 16.5f, H3Size = 13.5f;
    private const float H1Min = (H1Size + H2Size) / 2, H2Min = (H2Size + H3Size) / 2, H3Min = (H3Size + BodySize) / 2;
    public const float QuoteIndent = 18f, CodeIndent = 12f;
    public const string MonoFont = "Cascadia Mono";
    public const char UncheckedBox = '☐', CheckedBox = '☑';

    public static bool IsMono(string? font) =>
        font is not null && (font.Contains("Mono", StringComparison.OrdinalIgnoreCase)
                             || font.Contains("Consolas", StringComparison.OrdinalIgnoreCase)
                             || font.Contains("Courier", StringComparison.OrdinalIgnoreCase));

    public static float HeadingSize(int level) => level switch { 1 => H1Size, 2 => H2Size, _ => H3Size };

    private static bool Near(float value, float target) => Math.Abs(value - target) < 3f;

    /// <summary>Story position just before the final (undeletable) paragraph mark.</summary>
    private static int End(RichEditTextDocument d) => Math.Max(0, d.GetRange(0, int.MaxValue).EndPosition - 1);

    // ---- load -----------------------------------------------------------------------------

    public static void Load(RichEditBox box, PageDocument doc, string bodyFont)
    {
        var d = box.Document;
        d.SetText(TextSetOptions.None, "");
        var all = d.GetRange(0, int.MaxValue);
        ApplyChar(all, bodyFont, BodySize, false, false, false);
        ResetParagraph(all);

        for (var i = 0; i < doc.Blocks.Count; i++)
        {
            var block = doc.Blocks[i];
            if (i > 0) Append(d, "\r", r => ApplyChar(r, bodyFont, BodySize, false, false, false));
            var start = End(d);
            var size = block.Type == BlockType.Heading ? HeadingSize(block.Level) : BodySize;
            var isCode = block.Type == BlockType.Code;

            if (block.Type == BlockType.Todo)
                Append(d, (block.Checked ? CheckedBox : UncheckedBox) + " ", r => ApplyChar(r, bodyFont, BodySize, false, false, false));

            foreach (var span in block.Spans)
            {
                var text = isCode ? span.Text.Replace("\r\n", "\r").Replace('\n', '\r') : span.Text;
                Append(d, text, r =>
                {
                    ApplyChar(r, bodyFont, size, block.Type == BlockType.Heading || span.Bold, span.Italic || block.Type == BlockType.Quote, isCode || span.Code);
                    if (span.Link is not null && !isCode) r.Link = "\"" + span.Link + "\"";
                });
            }

            var para = d.GetRange(start, Math.Max(start, End(d)));
            para.Expand(TextRangeUnit.Paragraph);
            ApplyBlockParagraph(para, block.Type, bodyFont);
            if (block.Spans.Count == 0 || block.Text.Length == 0)
            {
                // Empty paragraph: put the block's formatting on its paragraph mark so it reads back correctly.
                ApplyChar(para, bodyFont, size, block.Type == BlockType.Heading, false, isCode);
            }
        }

        d.Selection.SetRange(0, 0);
        d.ClearUndoRedoHistory();
    }

    private static void Append(RichEditTextDocument d, string text, Action<ITextRange> format)
    {
        if (text.Length == 0) return;
        var pos = End(d);
        var r = d.GetRange(pos, pos);
        r.SetText(TextSetOptions.None, text);
        format(r);
    }

    public static void ApplyChar(ITextRange r, string bodyFont, float size, bool bold, bool italic, bool mono)
    {
        var cf = r.CharacterFormat;
        cf.Name = mono ? MonoFont : bodyFont;
        cf.Size = size;
        cf.Bold = bold ? FormatEffect.On : FormatEffect.Off;
        cf.Italic = italic ? FormatEffect.On : FormatEffect.Off;
        cf.Underline = UnderlineType.None;
    }

    public static void ResetParagraph(ITextRange r)
    {
        var pf = r.ParagraphFormat;
        pf.ListType = MarkerType.None;
        pf.SetIndents(0, 0, 0);
        pf.SpaceBefore = 0;
        pf.SpaceAfter = 6;
    }

    /// <summary>Applies paragraph-level formatting for a block type (does not touch prefixes).</summary>
    public static void ApplyBlockParagraph(ITextRange para, BlockType type, string bodyFont)
    {
        ResetParagraph(para);
        var pf = para.ParagraphFormat;
        switch (type)
        {
            case BlockType.Heading:
                pf.SpaceBefore = 14;
                pf.SpaceAfter = 4;
                break;
            case BlockType.Todo:
                pf.SpaceAfter = 3;
                break;
            case BlockType.Bulleted:
                pf.ListType = MarkerType.Bullet;
                pf.ListTab = 18;
                pf.SpaceAfter = 3;
                break;
            case BlockType.Numbered:
                pf.ListType = MarkerType.Arabic;
                pf.ListStyle = MarkerStyle.Period;
                pf.ListStart = 1;
                pf.ListTab = 22;
                pf.SpaceAfter = 3;
                break;
            case BlockType.Quote:
                pf.SetIndents(0, QuoteIndent, 0);
                break;
            case BlockType.Code:
                pf.SetIndents(0, CodeIndent, 0);
                pf.SpaceAfter = 0;
                para.CharacterFormat.Name = MonoFont;
                break;
        }
    }

    // ---- read -----------------------------------------------------------------------------

    private sealed record Run(string Text, bool Bold, bool Italic, bool Mono, string? Link, float Size);

    public static PageDocument Read(RichEditBox box)
    {
        var d = box.Document;
        var doc = new PageDocument();
        var storyEnd = d.GetRange(0, int.MaxValue).EndPosition;
        var pos = 0;
        while (pos < storyEnd)
        {
            var para = d.GetRange(pos, pos);
            para.Expand(TextRangeUnit.Paragraph);
            var ps = Math.Max(para.StartPosition, pos);
            var pe = para.EndPosition;
            if (pe <= pos) break;
            var block = ReadParagraph(d, ps, pe - 1);
            if (block.Type == BlockType.Code && doc.Blocks.Count > 0 && doc.Blocks[^1].Type == BlockType.Code)
                doc.Blocks[^1].Spans.Add(new Span("\n" + block.Text));
            else
                doc.Blocks.Add(block);
            pos = pe;
        }
        while (doc.Blocks.Count > 0 && doc.Blocks[^1].Type == BlockType.Paragraph && doc.Blocks[^1].Text.Length == 0)
            doc.Blocks.RemoveAt(doc.Blocks.Count - 1);
        return doc.Normalize();
    }

    /// <summary>Classifies the paragraph at a story position (used by toolbar toggles and Enter handling).</summary>
    public static Block ReadParagraphAt(RichEditTextDocument d, int position)
    {
        var para = d.GetRange(position, position);
        para.Expand(TextRangeUnit.Paragraph);
        return ReadParagraph(d, para.StartPosition, para.EndPosition - 1);
    }

    private static Block ReadParagraph(RichEditTextDocument d, int start, int contentEnd)
    {
        var pf = d.GetRange(start, start).ParagraphFormat;
        var listType = pf.ListType;
        var indent = pf.LeftIndent;

        var runs = new List<Run>();
        var p = start;
        while (p < contentEnd)
        {
            var probe = d.GetRange(p, p + 1);
            probe.Expand(TextRangeUnit.CharacterFormat);
            var s = Math.Max(probe.StartPosition, p);
            var e = Math.Min(probe.EndPosition, contentEnd);
            if (e <= s) e = Math.Min(s + 1, contentEnd);
            var run = d.GetRange(s, e);
            var cf = run.CharacterFormat;
            if (cf.Hidden != FormatEffect.On)
            {
                run.GetText(TextGetOptions.NoHidden, out var text);
                text = text.Replace('\r', ' ').Replace('\v', ' ').Replace('\n', ' ');
                if (text.Length > 0)
                {
                    var link = run.Link?.Trim().Trim('"');
                    runs.Add(new Run(text, cf.Bold == FormatEffect.On, cf.Italic == FormatEffect.On, IsMono(cf.Name),
                        string.IsNullOrWhiteSpace(link) ? null : link, cf.Size));
                }
            }
            p = e;
        }

        // Size and font of the first character (or the paragraph mark when empty) determine headings / code.
        float leadSize;
        bool leadMono;
        if (runs.Count > 0) { leadSize = runs[0].Size; leadMono = runs.All(r => r.Mono); }
        else
        {
            var mark = d.GetRange(contentEnd, contentEnd + 1).CharacterFormat;
            leadSize = mark.Size;
            leadMono = IsMono(mark.Name);
        }

        var block = new Block();
        var text0 = string.Concat(runs.Select(r => r.Text));
        if (text0.Length >= 2 && (text0[0] == UncheckedBox || text0[0] == CheckedBox) && text0[1] == ' ')
        {
            block.Type = BlockType.Todo;
            block.Checked = text0[0] == CheckedBox;
            runs = DropPrefix(runs, 2);
        }
        else if (listType == MarkerType.Bullet) block.Type = BlockType.Bulleted;
        else if (listType != MarkerType.None && listType != MarkerType.Undefined) block.Type = BlockType.Numbered;
        else if (leadSize >= H1Min) { block.Type = BlockType.Heading; block.Level = 1; }
        else if (leadSize >= H2Min) { block.Type = BlockType.Heading; block.Level = 2; }
        else if (leadSize >= H3Min) { block.Type = BlockType.Heading; block.Level = 3; }
        else if (leadMono && Near(indent, CodeIndent)) block.Type = BlockType.Code;
        else if (Near(indent, QuoteIndent)) block.Type = BlockType.Quote;

        foreach (var r in runs)
        {
            block.Spans.Add(block.Type switch
            {
                BlockType.Code => new Span(r.Text),
                BlockType.Heading => new Span { Text = r.Text, Italic = r.Italic, Code = r.Mono, Link = r.Link },
                // Quotes are displayed italic, so italic carries no meaning inside them.
                BlockType.Quote => new Span { Text = r.Text, Bold = r.Bold, Code = r.Mono, Link = r.Link },
                _ => new Span { Text = r.Text, Bold = r.Bold, Italic = r.Italic, Code = r.Mono, Link = r.Link },
            });
        }
        return block;
    }

    private static List<Run> DropPrefix(List<Run> runs, int count)
    {
        var result = new List<Run>();
        foreach (var r in runs)
        {
            if (count <= 0) { result.Add(r); continue; }
            if (r.Text.Length <= count) { count -= r.Text.Length; continue; }
            result.Add(r with { Text = r.Text[count..] });
            count = 0;
        }
        return result;
    }
}
