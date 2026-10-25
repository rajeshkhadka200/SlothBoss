using System.Globalization;
using System.Reflection;

namespace SlothBoss.Core;

public static class Ids
{
    public static string New() => Guid.NewGuid().ToString("D");

    public static bool IsValid(string? value) =>
        value is not null
        && value.Length == 36
        && Guid.TryParseExact(value, "D", out _)
        && value == value.ToLowerInvariant();
}

public static class Clock
{
    /// <summary>Overridable for tests.</summary>
    public static Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    public static string Stamp() => Format(Now());

    public static string Format(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    public static string Today() => Now().ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static bool IsValidDate(string? s) =>
        s is not null && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    public static bool IsValidTimestamp(string? s) =>
        s is not null && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out _);
}

internal static class Resources
{
    private static readonly Assembly Asm = typeof(Resources).Assembly;

    public static string Read(string logicalName)
    {
        using var stream = Asm.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Missing embedded resource '{logicalName}'.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static IEnumerable<string> Names(string prefix) =>
        Asm.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal);
}

/// <summary>A user-presentable failure (validation, missing record, unsafe operation).</summary>
public class SlothBossException(string message, Exception? inner = null) : Exception(message, inner);
