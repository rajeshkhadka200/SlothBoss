using System.Text.Json.Nodes;
using SlothBoss.Core.Agent.Tools;
using SlothBoss.Core.Ai;

namespace SlothBoss.Core.Agent;

/// <summary>
/// The single way tools read a file's content. Text formats (PDF, DOCX, PPTX, XLSX, TXT/MD/CSV) are
/// extracted locally; images are described by the local vision model (Gemma), including any visible text.
/// Image descriptions are cached in the run's artifacts so each image is looked at once per job.
/// </summary>
public static class ContentReader
{
    public const long MaxImageBytes = 15L * 1024 * 1024;

    public static async Task<ExtractedText?> ReadAsync(ToolContext ctx, string relPath, List<string> errors, int maxChars, CancellationToken ct)
    {
        var ext = Path.GetExtension(relPath).ToLowerInvariant();
        if (!DocumentText.IsImage(ext))
        {
            try
            {
                var text = DocumentText.Extract(ctx.Sandbox.Resolve(relPath), maxChars);
                if (text.Text.Trim().Length < 20) { errors.Add($"“{relPath}” has no readable text (a scanned PDF needs OCR, which isn't supported yet)."); return null; }
                return text;
            }
            catch (Exception e) when (e is SlothBossException or IOException or UnauthorizedAccessException)
            {
                errors.Add(e.Message);
                return null;
            }
        }

        var described = await DescribeImageAsync(ctx, relPath, errors, ct).ConfigureAwait(false);
        return described is null ? null : new ExtractedText(described, false, null);
    }

    /// <summary>Asks the local vision model what the image shows and what text it contains. Returns null (with an error) if it can't.</summary>
    public static async Task<string?> DescribeImageAsync(ToolContext ctx, string relPath, List<string> errors, CancellationToken ct)
    {
        var full = ctx.Sandbox.Resolve(relPath);
        var info = new FileInfo(full);
        if (!info.Exists) { errors.Add($"“{relPath}” no longer exists."); return null; }
        var cacheKey = $"{relPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        var cache = ctx.Artifacts["image_descriptions"] as JsonObject ?? [];
        ctx.Artifacts["image_descriptions"] = cache;
        if (cache[cacheKey]?.GetValue<string>() is { } cached) return cached;

        if (info.Length > MaxImageBytes) { errors.Add($"“{relPath}” is too large to look at ({info.Length / 1024 / 1024} MB)."); return null; }
        var model = ctx.Model;
        if (model is null) { errors.Add($"“{relPath}”: looking at images needs the local AI model."); return null; }
        if (!await model.SupportsVisionAsync(ct).ConfigureAwait(false))
        {
            errors.Add($"“{relPath}”: the selected model can't look at images. Choose a Gemma model with vision (for example gemma4 or gemma3 4B+).");
            return null;
        }

        var image = Convert.ToBase64String(await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false));
        ctx.CountModelCall();
        string text;
        try
        {
            text = (await model.ChatAsync(
                [new ChatMessage("system", Prompts.System), new ChatMessage("user", Prompts.Render("agent_describe_image", new Dictionary<string, string>()), [image])],
                null, ct).ConfigureAwait(false)).Trim();
        }
        catch (OllamaException e) when (e is not OllamaUnavailableException and not InferenceTimeoutException)
        {
            errors.Add($"“{relPath}”: the model couldn't open this image ({e.Message}).");
            return null;
        }
        if (text.Length < 10) { errors.Add($"“{relPath}”: the model returned no description."); return null; }
        if (text.Length > 1500) text = text[..1500];
        cache[cacheKey] = text;
        ctx.PersistProgress();
        return text;
    }
}
