using System.Text.Json.Nodes;
using SlothBoss.Core.Ai;

namespace SlothBoss.Core.Agent;

/// <summary>
/// Provider abstraction for the agent's reasoning model. Only local providers exist; the runtime
/// never substitutes a different provider when this one fails.
/// </summary>
public interface IAgentModel
{
    /// <summary>Shown to the user, e.g. "gemma4:e2b-it-qat (local, Ollama)".</summary>
    string DisplayName { get; }
    string PlannerId { get; }
    bool IsLocal { get; }
    Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages, JsonNode? format, CancellationToken ct);
    /// <summary>True if the model can look at images (Ollama "vision" capability).</summary>
    Task<bool> SupportsVisionAsync(CancellationToken ct);
}

/// <summary>Gemma (or another open-weight model) served by the local Ollama instance.</summary>
public sealed class OllamaAgentModel(OllamaClient client, string model) : IAgentModel
{
    public string DisplayName => $"{model} (local, Ollama)";
    public string PlannerId => "gemma:" + model;
    public bool IsLocal => true; // OllamaClient refuses non-loopback endpoints.

    public async Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages, JsonNode? format, CancellationToken ct) =>
        (await client.ChatAsync(new ChatRequest(model, messages, format, Temperature: 0.1), null, ct).ConfigureAwait(false)).Content;

    public async Task<bool> SupportsVisionAsync(CancellationToken ct) =>
        (await client.GetCapabilitiesAsync(model, ct).ConfigureAwait(false)).Contains("vision");
}

/// <summary>Structured, budgeted model calls with bounded corrective retries.</summary>
public static class ModelCalls
{
    public const int MaxAttempts = 3;

    public static async Task<T> StructuredAsync<T>(IAgentModel model, string prompt, string schemaName, Func<string, T> parse,
        Action beforeEachCall, CancellationToken ct)
    {
        var messages = new List<ChatMessage> { new("system", Prompts.System), new("user", prompt) };
        var format = Prompts.OutputSchema(schemaName);
        string? lastError = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            beforeEachCall();
            var reply = await model.ChatAsync(messages, format, ct).ConfigureAwait(false);
            try { return parse(reply); }
            catch (ModelOutputException e)
            {
                lastError = e.Message;
                messages.Add(new("assistant", reply.Length > 2000 ? reply[..2000] : reply));
                messages.Add(new("user", $"That reply was invalid: {e.Message} Reply again with only the corrected JSON object."));
            }
        }
        throw new ModelOutputException($"The model did not return valid output after {MaxAttempts} attempts ({lastError}).");
    }
}
