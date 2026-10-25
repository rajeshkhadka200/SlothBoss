using System.IO.Compression;
using System.Text;
using System.Xml;
using UglyToad.PdfPig;

namespace SlothBoss.Core.Agent;

public sealed record ExtractedText(string Text, bool Truncated, int? Pages);

/// <summary>
/// Plain-text extraction for a deliberately small set of formats: .txt .md .csv .log .json (as text),
/// .pdf (text layer via PdfPig; scanned PDFs without a text layer yield nothing here — no OCR), .docx,
/// .pptx (slide text + speaker notes) and .xlsx (every sheet's cells). Images are handled by ContentReader.
/// Everything returned is untrusted data.
/// </summary>
public static class DocumentText
{
    public static readonly string[] TextExtensions = [".txt", ".md", ".markdown", ".csv", ".log", ".json"];
    public static readonly string[] OfficeExtensions = [".docx", ".pptx", ".xlsx"];
    /// <summary>Images are "read" by the local vision model (see <see cref="ContentReader"/>), not here.</summary>
    public static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"];
    public static readonly string[] SupportedExtensions = [.. TextExtensions, ".pdf", .. OfficeExtensions, .. ImageExtensions];
    public const long MaxFileBytes = 25L * 1024 * 1024;

    public static bool IsSupported(string extension) => SupportedExtensions.Contains(extension.ToLowerInvariant());
    public static bool IsImage(string extension) => ImageExtensions.Contains(extension.ToLowerInvariant());

    public static ExtractedText Extract(string fullPath, int maxChars = 12000)
    {
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new SlothBossException($"{info.Name} no longer exists.");
        if (info.Length > MaxFileBytes) throw new SlothBossException($"{info.Name} is larger than {MaxFileBytes / 1024 / 1024} MB; skipped.");
        var ext = info.Extension.ToLowerInvariant();
        string text;
        int? pages = null;
        try
        {
            switch (ext)
            {
                case ".pdf":
                    (text, pages) = Pdf(fullPath, maxChars);
                    break;
                case ".docx":
                    text = Docx(fullPath);
                    break;
                case ".pptx":
                    (text, pages) = Pptx(fullPath);
                    break;
                case ".xlsx":
                    text = Xlsx(fullPath, maxChars * 2);
                    break;
                default:
                    if (!TextExtensions.Contains(ext)) throw new SlothBossException($"{info.Name}: {ext} files aren't supported yet.");
                    text = ReadText(fullPath, maxChars * 2);
                    break;
            }
        }
        catch (Exception e) when (e is not SlothBossException and not IOException)
        {
            throw new SlothBossException($"Couldn't read {info.Name}: {e.Message}");
        }
        text = Normalize(text);
        var truncated = text.Length > maxChars;
        return new ExtractedText(truncated ? text[..maxChars] : text, truncated, pages);
    }

    private static string ReadText(string path, int maxChars)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[maxChars];
        var read = reader.ReadBlock(buffer, 0, maxChars);
        return new string(buffer, 0, read);
    }

    private static (string, int) Pdf(string path, int maxChars)
    {
        using var doc = PdfDocument.Open(path);
        var sb = new StringBuilder();
        foreach (var page in doc.GetPages())
        {
            sb.AppendLine(string.Join(" ", page.GetWords().Select(w => w.Text)));
            if (sb.Length > maxChars * 2) break;
        }
        return (sb.ToString(), doc.NumberOfPages);
    }

    private static string Docx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml") ?? throw new SlothBossException("This .docx has no document body.");
        if (entry.Length > 50L * 1024 * 1024) throw new SlothBossException("This .docx is too large to read.");
        using var stream = entry.Open();
        using var xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var sb = new StringBuilder();
        xml.Read();
        while (!xml.EOF)
        {
            if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "t" && !xml.IsEmptyElement)
            {
                sb.Append(xml.ReadElementContentAsString()); // advances past </w:t>
                continue;
            }
            if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "tab") sb.Append('\t');
            else if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "p") sb.Append('\n');
            xml.Read();
        }
        return sb.ToString();
    }

    private static XmlReader OpenXml(ZipArchiveEntry entry)
    {
        if (entry.Length > 50L * 1024 * 1024) throw new SlothBossException("Part of this file is too large to read.");
        return XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CloseInput = true });
    }

    /// <summary>
    /// The text of each shape (p:sp) in a slide or notes part, with its placeholder type ("title", "body",
    /// "sldNum", … or null). Text runs (a:t) are joined, one line per paragraph (a:p).
    /// </summary>
    private static List<(string? Placeholder, string Text)> ShapeTexts(ZipArchiveEntry entry)
    {
        using var xml = OpenXml(entry);
        var shapes = new List<(string?, string)>();
        var sb = new StringBuilder();
        string? placeholder = null;
        var depth = 0; // nesting of p:sp (group shapes can't nest p:sp, but be safe)
        xml.Read();
        while (!xml.EOF)
        {
            if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "sp" && !xml.IsEmptyElement)
            {
                if (depth++ == 0) { sb.Clear(); placeholder = null; }
            }
            else if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "sp" && depth > 0)
            {
                if (--depth == 0 && sb.Length > 0) shapes.Add((placeholder, sb.ToString().Trim()));
            }
            else if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "ph" && depth > 0)
                placeholder = xml.GetAttribute("type") ?? "body"; // a placeholder without a type is the body
            else if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "t" && !xml.IsEmptyElement)
            {
                sb.Append(xml.ReadElementContentAsString());
                continue;
            }
            else if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "p" && xml.NamespaceURI.Contains("drawingml")) sb.Append('\n');
            xml.Read();
        }
        return shapes;
    }

    private static int PartNumber(string name, string prefix) =>
        int.TryParse(Path.GetFileNameWithoutExtension(name).AsSpan(prefix.Length), out var n) ? n : int.MaxValue;

    /// <summary>Relationship id → target part path (resolved against the source part's folder).</summary>
    private static Dictionary<string, (string Type, string Target)> Relationships(ZipArchive zip, string partPath)
    {
        var dir = Path.GetDirectoryName(partPath)!.Replace('\\', '/');
        var rels = new Dictionary<string, (string, string)>();
        if (zip.GetEntry($"{dir}/_rels/{Path.GetFileName(partPath)}.rels") is not { } entry) return rels;
        using var xml = OpenXml(entry);
        while (xml.Read())
        {
            if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "Relationship" || xml.GetAttribute("TargetMode") == "External") continue;
            var target = xml.GetAttribute("Target") ?? "";
            var parts = new List<string>(target.StartsWith('/') ? [] : dir.Split('/'));
            foreach (var seg in target.TrimStart('/').Split('/'))
                if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else if (seg is not ("." or "")) parts.Add(seg);
            rels[xml.GetAttribute("Id") ?? ""] = (xml.GetAttribute("Type") ?? "", string.Join('/', parts));
        }
        return rels;
    }

    /// <summary>PowerPoint: slide text in presentation order, plus each slide's speaker notes.</summary>
    private static (string, int) Pptx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        // Presentation order comes from presentation.xml (sldIdLst); file names are only a fallback.
        var order = new List<string>();
        if (zip.GetEntry("ppt/presentation.xml") is { } pres)
        {
            var rels = Relationships(zip, "ppt/presentation.xml");
            using var xml = OpenXml(pres);
            while (xml.Read())
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "sldId"
                    && xml.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships") is { } rid
                    && rels.TryGetValue(rid, out var rel) && zip.GetEntry(rel.Target) is not null)
                    order.Add(rel.Target);
        }
        if (order.Count == 0)
            order = zip.Entries.Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
                .OrderBy(e => PartNumber(e.Name, "slide")).Select(e => e.FullName).ToList();
        if (order.Count == 0) throw new SlothBossException("This .pptx has no slides.");

        var sb = new StringBuilder();
        for (var i = 0; i < order.Count; i++)
        {
            var slideText = ShapeTexts(zip.GetEntry(order[i])!).Where(s => s.Placeholder is not ("sldNum" or "dt" or "ftr")).Select(s => s.Text);
            sb.Append("Slide ").Append(i + 1).Append(":\n").Append(string.Join('\n', slideText)).Append('\n');
            // The notes page is linked from the slide's relationships, not by matching numbers.
            var notesRel = Relationships(zip, order[i]).Values.FirstOrDefault(r => r.Type.EndsWith("/notesSlide", StringComparison.Ordinal));
            if (notesRel.Target is { } notesPath && zip.GetEntry(notesPath) is { } notes)
            {
                var noteText = string.Join('\n', ShapeTexts(notes).Where(s => s.Placeholder is "body" or null).Select(s => s.Text)).Trim();
                if (noteText.Length > 0) sb.Append("Notes: ").Append(noteText).Append('\n');
            }
            sb.Append('\n');
        }
        return (sb.ToString(), order.Count);
    }

    /// <summary>Excel: each sheet's rows as tab-separated lines (shared strings and inline strings resolved).</summary>
    private static string Xlsx(string path, int maxChars)
    {
        using var zip = ZipFile.OpenRead(path);
        var shared = new List<string>();
        if (zip.GetEntry("xl/sharedStrings.xml") is { } sst)
        {
            using var xml = OpenXml(sst);
            var current = new StringBuilder();
            while (xml.Read())
            {
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "si") current.Clear();
                else if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "t" && !xml.IsEmptyElement) current.Append(xml.ReadElementContentAsString());
                if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "si") shared.Add(current.ToString());
            }
        }
        var dateStyles = DateStyles(zip);
        // Sheets in workbook order (name + part via relationships); file names are only a fallback.
        var sheets = new List<(string Name, string Part)>();
        if (zip.GetEntry("xl/workbook.xml") is { } wb)
        {
            var rels = Relationships(zip, "xl/workbook.xml");
            using var xml = OpenXml(wb);
            while (xml.Read())
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "sheet"
                    && xml.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships") is { } rid
                    && rels.TryGetValue(rid, out var rel) && zip.GetEntry(rel.Target) is not null)
                    sheets.Add((xml.GetAttribute("name") ?? $"Sheet{sheets.Count + 1}", rel.Target));
        }
        if (sheets.Count == 0)
            sheets = zip.Entries.Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) && e.FullName.EndsWith(".xml", StringComparison.Ordinal))
                .OrderBy(e => PartNumber(e.Name, "sheet")).Select((e, i) => ($"Sheet{i + 1}", e.FullName)).ToList();
        if (sheets.Count == 0) throw new SlothBossException("This .xlsx has no sheets.");

        var sb = new StringBuilder();
        foreach (var (name, part) in sheets)
        {
            if (sb.Length >= maxChars) break;
            sb.Append("Sheet: ").Append(name).Append('\n');
            using var xml = OpenXml(zip.GetEntry(part)!);
            var row = new List<string>();
            string? type = null;
            var style = 0;
            var column = 0;
            var cell = new StringBuilder();
            xml.Read();
            while (!xml.EOF && sb.Length < maxChars)
            {
                if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "row") row.Clear();
                else if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "c")
                {
                    type = xml.GetAttribute("t");
                    style = int.TryParse(xml.GetAttribute("s"), out var s) ? s : 0;
                    column = ColumnIndex(xml.GetAttribute("r")) ?? row.Count;
                    cell.Clear();
                }
                else if (xml.NodeType == XmlNodeType.Element && xml.LocalName is "v" or "t" && !xml.IsEmptyElement)
                {
                    cell.Append(xml.ReadElementContentAsString()); // advances past the end tag; rich inline strings have several <t> runs
                    continue;
                }
                else if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "c")
                {
                    while (row.Count < column) row.Add(""); // keep empty cells so columns stay aligned
                    row.Add(CellValue(cell.ToString(), type, style, shared, dateStyles));
                }
                else if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "row" && row.Any(c => c.Length > 0))
                    sb.Append(string.Join('\t', row)).Append('\n');
                xml.Read();
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string CellValue(string raw, string? type, int style, List<string> shared, HashSet<int> dateStyles) => type switch
    {
        "s" => int.TryParse(raw, out var idx) && idx >= 0 && idx < shared.Count ? shared[idx] : raw,
        "b" => raw == "1" ? "TRUE" : "FALSE",
        null or "n" when dateStyles.Contains(style) && double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var serial)
                        && serial is > 0 and < 2958466 => ExcelDate(serial),
        _ => raw,
    };

    private static string ExcelDate(double serial)
    {
        var d = DateTime.FromOADate(serial);
        return d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd") : serial < 1 ? d.ToString("HH:mm") : d.ToString("yyyy-MM-dd HH:mm");
    }

    /// <summary>"C12" → 2 (zero-based column).</summary>
    private static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        var n = 0;
        foreach (var ch in reference)
        {
            if (!char.IsAsciiLetter(ch)) break;
            n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return n == 0 || n > 16384 ? null : n - 1;
    }

    /// <summary>Indexes of cell styles (cellXfs) whose number format shows a date or time.</summary>
    private static HashSet<int> DateStyles(ZipArchive zip)
    {
        var result = new HashSet<int>();
        if (zip.GetEntry("xl/styles.xml") is not { } entry) return result;
        var custom = new Dictionary<int, string>();
        var xfs = new List<int>();
        using var xml = OpenXml(entry);
        var inCellXfs = false;
        while (xml.Read())
        {
            if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "numFmt" && int.TryParse(xml.GetAttribute("numFmtId"), out var id))
                custom[id] = xml.GetAttribute("formatCode") ?? "";
            else if (xml.NodeType == XmlNodeType.Element && xml.LocalName == "cellXfs") inCellXfs = !xml.IsEmptyElement;
            else if (xml.NodeType == XmlNodeType.EndElement && xml.LocalName == "cellXfs") inCellXfs = false;
            else if (inCellXfs && xml.NodeType == XmlNodeType.Element && xml.LocalName == "xf")
                xfs.Add(int.TryParse(xml.GetAttribute("numFmtId"), out var f) ? f : 0);
        }
        for (var i = 0; i < xfs.Count; i++)
        {
            var f = xfs[i];
            // Built-in date/time formats: 14–22, 45–47 (plus common East Asian locale ones 27–36, 50–58).
            if (f is >= 14 and <= 22 or >= 45 and <= 47 or >= 27 and <= 36 or >= 50 and <= 58) result.Add(i);
            else if (custom.TryGetValue(f, out var code) && IsDateFormat(code)) result.Add(i);
        }
        return result;
    }

    private static bool IsDateFormat(string code)
    {
        // Ignore quoted literals, escaped characters and [colour]/[locale] sections, then look for date/time letters.
        var stripped = System.Text.RegularExpressions.Regex.Replace(code, "\"[^\"]*\"|\\\\.|\\[[^\\]]*\\]", "");
        return stripped.IndexOfAny(['y', 'Y', 'd', 'D', 'h', 'H']) >= 0 || stripped.Contains("mm", StringComparison.OrdinalIgnoreCase) && stripped.Contains(':');
    }

    private static string Normalize(string text) =>
        string.Join("\n", text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd()))
              .Replace("\0", "").Trim();
}
