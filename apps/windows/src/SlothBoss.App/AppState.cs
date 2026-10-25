using System.Text.Json;
using Microsoft.UI.Dispatching;
using SlothBoss.Core;
using SlothBoss.Core.Agent;
using SlothBoss.Core.Agent.Tools;
using SlothBoss.Core.Agent.Web;
using SlothBoss.Core.Ai;
using SlothBoss.Core.Storage;

namespace SlothBoss.App;

/// <summary>Machine-level preferences kept outside the workspace (which workspace file to open).</summary>
public sealed class AppConfig
{
    public string WorkspacePath { get; set; } = Workspace.DefaultPath();

    public static string ConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SlothBoss", "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig();
        }
        catch (Exception e) when (e is JsonException or IOException) { }
        return new AppConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

public enum AiState { Checking, Ready, OllamaUnavailable, NoGemmaModel, ModelNotSelected, Error }

/// <summary>
/// Process-wide services: the open workspace, the local Ollama client and the AI availability state.
/// Workspace change notifications are re-raised on the UI thread.
/// </summary>
public sealed class AppState
{
    public static AppState Current { get; private set; } = null!;

    public Workspace Ws { get; private set; }
    public DispatcherQueue Dispatcher { get; }
    public OllamaClient Client { get; private set; }

    public AiState AiState { get; private set; } = AiState.Checking;
    public string AiMessage { get; private set; } = "Checking for Ollama…";
    public string? OllamaVersion { get; private set; }
    public IReadOnlyList<OllamaModel> GemmaModels { get; private set; } = [];
    public int IgnoredModelCount { get; private set; }
    public string? SelectedModel => Ws.Settings.Get(SettingsStore.SelectedModel);

    /// <summary>Raised on the UI thread after workspace writes.</summary>
    public event Action<WorkspaceChange>? Changed;
    public event Action? AiStatusChanged;

    public AgentRuntime Agent { get; private set; } = null!;
    public event Action<string>? AgentError;

    private AppState(Workspace ws, DispatcherQueue dispatcher)
    {
        Ws = ws;
        Dispatcher = dispatcher;
        Client = CreateClient();
        ws.Changed += OnWorkspaceChanged;
        CreateAgent();
    }

    /// <summary>The agent reasons with the selected local Gemma model only when Ollama reports it ready.</summary>
    private IAgentModel? CurrentModel() =>
        AiState == AiState.Ready && SelectedModel is { } m ? new OllamaAgentModel(Client, m) : null;

    /// <summary>Opt-in web research: off until the user enables it in Settings and provides keys.</summary>
    public WebConfig Web { get; } = new()
    {
        Enabled = () => Current?.Ws.Settings.Get(WebEnabledKey) == "true",
        SerpApiKey = () => SecretStore.Get(SecretStore.SerpApi),
        BrowserbaseKey = () => SecretStore.Get(SecretStore.Browserbase),
    };

    public const string WebEnabledKey = "web.enabled";
    public bool WebEnabled => Ws.Settings.Get(WebEnabledKey) == "true";

    private void CreateAgent()
    {
        Agent = new AgentRuntime(Ws, CurrentModel, ToolRegistry.Default(Web));
        Agent.BackgroundError += (_, e) => Dispatcher.TryEnqueue(() => AgentError?.Invoke(e.Message));
        // Runs that were working when the app closed are reconciled with the disk and left paused.
        var recovered = Agent.RecoverInterrupted(autoResume: Ws.Settings.Get("agent.auto_resume") == "true");
        RecoveredRuns = recovered.Count;
    }

    public int RecoveredRuns { get; private set; }

    public static void Initialize(Workspace ws, DispatcherQueue dispatcher) => Current = new AppState(ws, dispatcher);

    private void OnWorkspaceChanged(object? sender, WorkspaceChange change)
    {
        if (Dispatcher.HasThreadAccess) Changed?.Invoke(change);
        else Dispatcher.TryEnqueue(() => Changed?.Invoke(change));
    }

    private OllamaClient CreateClient()
    {
        Uri endpoint;
        try { endpoint = OllamaClient.ParseEndpoint(Ws.Settings.Get(SettingsStore.OllamaEndpoint, OllamaClient.DefaultEndpoint.ToString())); }
        catch (OllamaException) { endpoint = OllamaClient.DefaultEndpoint; }
        var timeout = int.TryParse(Ws.Settings.Get(SettingsStore.AiTimeoutSeconds), out var s) && s is >= 15 and <= 1800 ? s : 180;
        return new OllamaClient(endpoint) { Timeout = TimeSpan.FromSeconds(timeout) };
    }

    /// <summary>Re-reads endpoint/timeout settings and re-checks availability.</summary>
    public Task ReconfigureAiAsync()
    {
        Client.Dispose();
        Client = CreateClient();
        return RefreshAiAsync();
    }

    public void SwitchWorkspace(Workspace ws)
    {
        Ws.Changed -= OnWorkspaceChanged;
        Ws.Dispose();
        Ws = ws;
        Ws.Changed += OnWorkspaceChanged;
        CreateAgent();
        Changed?.Invoke(WorkspaceChange.All);
        _ = ReconfigureAiAsync();
    }

    /// <summary>Checks Ollama and the installed models. Never downloads anything.</summary>
    public async Task RefreshAiAsync()
    {
        SetAi(AiState.Checking, "Checking for Ollama…");
        try
        {
            OllamaVersion = await Client.GetVersionAsync();
            var models = await Client.ListModelsAsync();
            GemmaModels = models.Where(m => m.IsGemma).ToList();
            IgnoredModelCount = models.Count - GemmaModels.Count;
            if (GemmaModels.Count == 0)
            {
                SetAi(AiState.NoGemmaModel, "Ollama is running, but no Gemma model is installed.");
                return;
            }
            var selected = SelectedModel;
            if (selected is null || GemmaModels.All(m => m.Name != selected))
            {
                if (GemmaModels.Count == 1)
                {
                    Ws.Settings.Set(SettingsStore.SelectedModel, GemmaModels[0].Name);
                    selected = GemmaModels[0].Name;
                }
                else
                {
                    SetAi(AiState.ModelNotSelected, "Choose a Gemma model in Settings.");
                    return;
                }
            }
            SetAi(AiState.Ready, $"{selected} · local");
        }
        catch (OllamaUnavailableException)
        {
            ClearModels();
            SetAi(AiState.OllamaUnavailable, "Ollama is not running.");
        }
        catch (OllamaException e)
        {
            ClearModels();
            SetAi(AiState.Error, e.Message);
        }
    }

    private void ClearModels()
    {
        OllamaVersion = null;
        GemmaModels = [];
        IgnoredModelCount = 0;
    }

    public void SelectModel(string name)
    {
        Ws.Settings.Set(SettingsStore.SelectedModel, name);
        _ = RefreshAiAsync();
    }

    private void SetAi(AiState state, string message)
    {
        AiState = state;
        AiMessage = message;
        AiStatusChanged?.Invoke();
    }

    /// <summary>A ready-to-use assistant, or null when AI is unavailable.</summary>
    public AssistantService? CreateAssistant() =>
        AiState == AiState.Ready && SelectedModel is { } model ? new AssistantService(Ws, Client, model) : null;

    /// <summary>Re-checks after an inference error that suggests the server or model went away.</summary>
    public void ReportAiFailure(Exception e)
    {
        if (e is OllamaUnavailableException or ModelNotFoundException) _ = RefreshAiAsync();
    }
}
