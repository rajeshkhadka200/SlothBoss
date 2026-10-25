using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace SlothBoss.Core.Agent;

public sealed class SandboxViolationException(string message) : SlothBossException(message);

public sealed record FileEntry(string RelPath, string Name, string Extension, long Size, string ModifiedAt, int Depth);

/// <summary>
/// Confines every agent file operation to one user-approved folder. All paths that cross the tool
/// boundary are relative to the root and are re-validated here: no absolute paths, no "..", no
/// alternate data streams, no reserved device names, and no symbolic links or junctions anywhere on
/// the way (so a link inside the folder can't point the agent somewhere else).
/// Nothing in here deletes or overwrites a file.
/// </summary>
public sealed partial class FileSandbox
{
    public string Root { get; }

    public FileSandbox(string root)
    {
        Root = NormalizeRoot(root);
        if (!Directory.Exists(Root)) throw new SandboxViolationException($"The approved folder no longer exists: {Root}");
        if (IsLink(new DirectoryInfo(Root))) throw new SandboxViolationException("The approved folder is a link; choose the real folder instead.");
    }

    public static string NormalizeRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new SandboxViolationException("No folder was given.");
        var full = Path.GetFullPath(path.Trim());
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new SandboxViolationException("Device paths can't be approved.");
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(trimmed, Path.GetPathRoot(full)?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new SandboxViolationException("A whole drive can't be approved; choose a specific folder.");
        var system = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.System),
        }.Where(s => !string.IsNullOrEmpty(s));
        if (system.Any(s => trimmed.StartsWith(s.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
            throw new SandboxViolationException("System folders can't be approved for the agent.");
        return trimmed;
    }

    private static readonly string[] ReservedNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    private static bool IsLink(FileSystemInfo info) => info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>Validates a root-relative path and returns its absolute form. Throws on anything suspicious.</summary>
    public string Resolve(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) throw new SandboxViolationException("Empty path.");
        var rel = relative.Replace('/', '\\').Trim();
        if (Path.IsPathRooted(rel) || rel.StartsWith('\\')) throw new SandboxViolationException($"'{relative}' is not inside the approved folder.");
        var segments = rel.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        foreach (var seg in segments)
        {
            if (seg is "." or "..") throw new SandboxViolationException($"'{relative}' tries to leave the approved folder.");
            if (seg.Contains(':')) throw new SandboxViolationException($"'{relative}' is not a plain file path.");
            if (seg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new SandboxViolationException($"'{relative}' contains characters that aren't allowed in file names.");
            if (seg.EndsWith('.') || seg.EndsWith(' ')) throw new SandboxViolationException($"'{relative}' has a name ending in a dot or space.");
            var stem = seg.Split('.')[0].ToUpperInvariant();
            if (ReservedNames.Contains(stem)) throw new SandboxViolationException($"'{relative}' uses a reserved device name.");
        }
        var full = Path.GetFullPath(Path.Combine(Root, string.Join('\\', segments)));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new SandboxViolationException($"'{relative}' is not inside the approved folder.");

        // No links anywhere between the root and the target.
        var probe = Root;
        foreach (var seg in segments)
        {
            probe = Path.Combine(probe, seg);
            if (IsLink(new FileInfo(probe)) || IsLink(new DirectoryInfo(probe)))
                throw new SandboxViolationException($"'{relative}' goes through a link, which the agent doesn't follow.");
        }
        return full;
    }

    public string ToRelative(string full) =>
        Path.GetRelativePath(Root, full).Replace('/', '\\');

    public bool FileExists(string rel) => File.Exists(Resolve(rel));
    public bool DirectoryExists(string rel) => Directory.Exists(Resolve(rel));

    /// <summary>Lists files breadth-first up to <paramref name="maxDepth"/> levels, skipping hidden, system and linked entries.</summary>
    public List<FileEntry> List(int maxDepth, int maxFiles, out bool truncated)
    {
        truncated = false;
        var result = new List<FileEntry>();
        var queue = new Queue<(DirectoryInfo Dir, int Depth)>();
        queue.Enqueue((new DirectoryInfo(Root), 0));
        while (queue.Count > 0)
        {
            var (dir, depth) = queue.Dequeue();
            FileSystemInfo[] children;
            try { children = dir.GetFileSystemInfos(); }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException) { continue; }
            foreach (var child in children.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (child.Attributes.HasFlag(FileAttributes.Hidden) || child.Attributes.HasFlag(FileAttributes.System) || IsLink(child)) continue;
                if (child is DirectoryInfo sub)
                {
                    if (depth + 1 < maxDepth) queue.Enqueue((sub, depth + 1));
                    continue;
                }
                if (child is FileInfo f)
                {
                    if (result.Count >= maxFiles) { truncated = true; return result; }
                    result.Add(new FileEntry(ToRelative(f.FullName), f.Name, f.Extension.ToLowerInvariant(), f.Length,
                        Clock.Format(f.LastWriteTimeUtc), depth));
                }
            }
        }
        return result;
    }

    public static string Sha256(string fullPath)
    {
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public string HashRel(string rel) => Sha256(Resolve(rel));

    [GeneratedRegex(@"[\x00-\x1F<>:""/\\|?*]")] private static partial Regex BadChars();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();

    /// <summary>Turns a model- or user-suggested name into a safe single path segment (no separators), or null.</summary>
    public static string? SafeSegment(string? name, int maxLength = 80)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var s = Spaces().Replace(BadChars().Replace(name, " "), " ").Trim().Trim('.', ' ');
        if (s.Length == 0 || s is "." or "..") return null;
        if (s.Length > maxLength) s = s[..maxLength].Trim().Trim('.');
        if (ReservedNames.Contains(s.Split('.')[0].ToUpperInvariant())) s = "_" + s;
        return s.Length == 0 ? null : s;
    }

    /// <summary>A target path that doesn't exist yet and isn't already claimed, adding " (2)", " (3)"… if needed.</summary>
    public string UniqueTarget(string relTarget, ISet<string> claimed)
    {
        var dir = Path.GetDirectoryName(relTarget) ?? "";
        var stem = Path.GetFileNameWithoutExtension(relTarget);
        var ext = Path.GetExtension(relTarget);
        var candidate = relTarget;
        for (var n = 2; File.Exists(Resolve(candidate)) || Directory.Exists(Resolve(candidate)) || claimed.Contains(candidate.ToLowerInvariant()); n++)
        {
            if (n > 999) throw new SlothBossException($"Couldn't find a free name for {relTarget}.");
            candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
        }
        claimed.Add(candidate.ToLowerInvariant());
        return candidate;
    }

    /// <summary>Moves a file without ever overwriting. Throws <see cref="IOException"/> if the target exists.</summary>
    public void MoveNoOverwrite(string sourceRel, string targetRel)
    {
        var src = Resolve(sourceRel);
        var dst = Resolve(targetRel);
        if (File.Exists(dst) || Directory.Exists(dst)) throw new FileCollisionException(targetRel);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Move(src, dst, overwrite: false);
    }

    /// <summary>Writes a new file; refuses if anything already exists at the path.</summary>
    public void CreateNewFile(string targetRel, string content)
    {
        var dst = Resolve(targetRel);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        using var stream = new FileStream(dst, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false));
        writer.Write(content);
    }
}

public sealed class FileCollisionException(string target)
    : IOException($"Something already exists at '{target}', so nothing was overwritten.")
{
    public string Target { get; } = target;
}
