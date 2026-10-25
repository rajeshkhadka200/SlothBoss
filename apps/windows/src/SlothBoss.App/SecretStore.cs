using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SlothBoss.App;

/// <summary>
/// API keys for optional web services, encrypted with Windows DPAPI for the current user
/// (%LOCALAPPDATA%\SlothBoss\secrets.bin). Never stored in the workspace database, backups or logs.
/// Environment variables (SERPAPI_API_KEY, BROWSERBASE_API_KEY) are used when no key was saved.
/// </summary>
public static class SecretStore
{
    public const string SerpApi = "SERPAPI_API_KEY";
    public const string Browserbase = "BROWSERBASE_API_KEY";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SlothBoss.secrets.v1");
    private static string FilePath => Path.Combine(Path.GetDirectoryName(AppConfig.ConfigPath)!, "secrets.bin");
    private static readonly Lock Gate = new();

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(plain) ?? [];
        }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException) { return []; }
    }

    /// <summary>The saved key, or the environment variable of the same name, or null.</summary>
    public static string? Get(string name)
    {
        lock (Gate)
        {
            if (Load().TryGetValue(name, out var v) && v.Length > 0) return v;
        }
        var env = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    public static bool IsSaved(string name) { lock (Gate) return Load().ContainsKey(name); }
    public static bool FromEnvironment(string name) => !IsSaved(name) && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));

    public static void Set(string name, string? value)
    {
        lock (Gate)
        {
            var all = Load();
            if (string.IsNullOrWhiteSpace(value)) all.Remove(name); else all[name] = value.Trim();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var cipher = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(all), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(FilePath, cipher);
        }
    }

    /// <summary>For display only: "••••1a2b".</summary>
    public static string Mask(string? key) => key is null ? "not set" : key.Length <= 4 ? "••••" : "••••" + key[^4..];
}
