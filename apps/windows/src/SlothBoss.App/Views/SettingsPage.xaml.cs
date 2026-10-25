using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using SlothBoss.App.Helpers;
using SlothBoss.Core;
using SlothBoss.Core.Ai;
using SlothBoss.Core.Storage;

namespace SlothBoss.App.Views;

public sealed partial class SettingsPage : Microsoft.UI.Xaml.Controls.Page
{
    private static AppState State => AppState.Current;
    private static Workspace Ws => AppState.Current.Ws;
    private bool _loading;

    public static string AppVersion => typeof(SettingsPage).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public SettingsPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _loading = true;
        EndpointBox.Text = Ws.Settings.Get(SettingsStore.OllamaEndpoint, OllamaClient.DefaultEndpoint.ToString());
        TimeoutBox.Value = State.Client.Timeout.TotalSeconds;
        ThemeChoice.SelectedIndex = Ws.Settings.Get(SettingsStore.Theme, "system") switch { "light" => 1, "dark" => 2, _ => 0 };
        _loading = false;
        RefreshWorkspace();
        ShortcutText.Text = string.Join("\n", [
            "Ctrl+N            New page              Ctrl+Shift+N   New subpage",
            "Ctrl+P            Search                Ctrl+J         Toggle assistant",
            "Ctrl+Shift+T      Tasks                 Ctrl+Shift+H   Home",
            "Ctrl+,            Settings              Ctrl+S         Save now",
            "Ctrl+B / Ctrl+I   Bold / italic         Ctrl+E         Inline code",
            "Ctrl+K            Link                  Ctrl+Shift+K   Link to page",
            "Ctrl+1/2/3        Heading 1/2/3         Ctrl+0         Normal text",
            "Ctrl+Shift+8      Bulleted list         Ctrl+Shift+7   Numbered list",
            "Ctrl+Shift+9      Checklist             Ctrl+Enter     Toggle checkbox",
            "Ctrl+Shift+Q      Quote                 Ctrl+Shift+C   Code block",
            "Ctrl+Z / Ctrl+Y   Undo / redo           Ctrl+Enter     Send (assistant)",
            "Typing shortcuts: '# ' '## ' '### ' '- ' '1. ' '[] ' '> ' '```' at the start of a line.",
        ]);
        VersionText.Text = $"SlothBoss for Windows {AppVersion} · MIT License · schema v{Workspace.SchemaVersion}";
        State.AiStatusChanged += UpdateAi;
        UpdateAi();
        _loading = true;
        AutoResumeToggle.IsOn = Ws.Settings.Get("agent.auto_resume") == "true";
        WebToggle.IsOn = State.WebEnabled;
        _loading = false;
        RenderAgentPermissions();
        RenderWebStatus();
    }

    // ---- web research ----------------------------------------------------------------------------

    private static string KeyStatus(string name) =>
        SecretStore.Get(name) is { } k ? $"Key {SecretStore.Mask(k)} ({(SecretStore.FromEnvironment(name) ? "from environment variable" : "saved, encrypted")})" : "No key set.";

    private void RenderWebStatus()
    {
        SerpStatus.Text = KeyStatus(SecretStore.SerpApi);
        BbStatus.Text = KeyStatus(SecretStore.Browserbase);
        VoiceStatus.Text = KeyStatus(VoiceInput.KeyName);
    }

    private void OnOpenArchive(object sender, RoutedEventArgs e) => App.Shell!.ShowArchive();
    private void OnOpenHistory(object sender, RoutedEventArgs e) => App.Shell!.ShowHistory();
    private void OnOpenSearch(object sender, RoutedEventArgs e) => App.Shell!.ShowSearch();

    private void OnSaveVoiceKey(object sender, RoutedEventArgs e) => SaveKey(VoiceInput.KeyName, VoiceKeyBox);

    private async void OnTestVoice(object sender, RoutedEventArgs e)
    {
        if (SecretStore.Get(VoiceInput.KeyName) is not { } key) { VoiceStatus.Text = "No key set."; return; }
        VoiceStatus.Text = "Sending a test sentence to ElevenLabs…";
        try
        {
            // A sentence spoken by Windows' built-in voice, so the test needs no microphone.
            using var synth = new Windows.Media.SpeechSynthesis.SpeechSynthesizer();
            using var speech = await synth.SynthesizeTextToStreamAsync("Organize my downloads folder.");
            await using var audio = speech.AsStreamForRead();
            using var client = new SlothBoss.Core.Agent.Web.ElevenLabsClient(key);
            var text = await client.TranscribeAsync(audio, "test.wav", default);
            VoiceStatus.Text = text.Contains("download", StringComparison.OrdinalIgnoreCase)
                ? $"✓ Key works. ElevenLabs heard: “{text}”"
                : $"Key accepted, but the transcript was unexpected: “{text}”";
        }
        catch (SlothBoss.Core.Agent.Web.WebServiceException ex) { VoiceStatus.Text = "✗ " + ex.Message; }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            VoiceStatus.Text = "Couldn't create the test sentence on this PC: " + ex.Message;
        }
    }

    private void OnWebToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Ws.Settings.Set(AppState.WebEnabledKey, WebToggle.IsOn ? "true" : null);
        if (WebToggle.IsOn && (SecretStore.Get(SecretStore.SerpApi) is null || SecretStore.Get(SecretStore.Browserbase) is null))
            App.Shell!.ShowInfo("Web research is on, but it needs both keys before jobs can search and read pages.", InfoBarSeverity.Warning);
    }

    private void OnSaveSerpKey(object sender, RoutedEventArgs e) => SaveKey(SecretStore.SerpApi, SerpKeyBox);
    private void OnSaveBbKey(object sender, RoutedEventArgs e) => SaveKey(SecretStore.Browserbase, BbKeyBox);

    private void SaveKey(string name, PasswordBox box)
    {
        try
        {
            var adding = !string.IsNullOrWhiteSpace(box.Password);
            SecretStore.Set(name, box.Password);
            box.Password = "";
            // Saving a search key means the user wants web research; turn it on (the switch stays visible).
            if (adding && name == SecretStore.SerpApi && !State.WebEnabled)
            {
                Ws.Settings.Set(AppState.WebEnabledKey, "true");
                _loading = true;
                WebToggle.IsOn = true;
                _loading = false;
            }
            RenderWebStatus();
            App.Shell!.ShowInfo(SecretStore.IsSaved(name) ? "Key saved (encrypted)." : "Saved key removed.", InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException)
        {
            App.Shell!.ShowError("Couldn't save the key", ex.Message);
        }
    }

    private async void OnTestSerp(object sender, RoutedEventArgs e)
    {
        if (SecretStore.Get(SecretStore.SerpApi) is not { } key) { SerpStatus.Text = "No key set."; return; }
        SerpStatus.Text = "Checking with SerpApi…";
        try
        {
            using var client = new SlothBoss.Core.Agent.Web.SerpApiClient(key);
            var account = await client.AccountAsync(default);
            SerpStatus.Text = $"✓ Key works. Plan: {account.PlanName ?? "unknown"}; searches left: {account.SearchesLeft?.ToString() ?? "unknown"}.";
        }
        catch (SlothBoss.Core.Agent.Web.WebServiceException ex) { SerpStatus.Text = "✗ " + ex.Message; }
    }

    private async void OnTestBb(object sender, RoutedEventArgs e)
    {
        if (SecretStore.Get(SecretStore.Browserbase) is not { } key) { BbStatus.Text = "No key set."; return; }
        BbStatus.Text = "Reading example.com through Browserbase…";
        try
        {
            using var client = new SlothBoss.Core.Agent.Web.BrowserbaseClient(key);
            var page = await client.FetchMarkdownAsync("https://example.com/", default);
            BbStatus.Text = page.Content.Contains("Example Domain") ? "✓ Key works: example.com was read." : $"Key accepted, but the page text was unexpected ({page.Content.Length} characters).";
        }
        catch (SlothBoss.Core.Agent.Web.WebServiceException ex) { BbStatus.Text = "✗ " + ex.Message; }
    }

    private void RenderAgentPermissions()
    {
        FolderList.Children.Clear();
        var folders = Ws.Agent.Folders();
        if (folders.Count == 0)
            FolderList.Children.Add(new TextBlock { Text = "None yet. Choose a folder on the Home screen.", Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"] });
        foreach (var f in folders)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock { Text = f.Path, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, IsTextSelectionEnabled = true });
            var revoke = new Button { Content = "Remove access" };
            revoke.Click += (_, _) => { App.Shell!.Run(() => Ws.Agent.RevokeFolder(f.Id)); RenderAgentPermissions(); };
            Grid.SetColumn(revoke, 1);
            row.Children.Add(revoke);
            FolderList.Children.Add(row);
        }

        PolicyList.Children.Clear();
        string[] labels = ["Default (follow the job's mode)", "Always ask me", "Allow automatically", "Never allow"];
        foreach (var tool in State.Agent.Tools)
        {
            var spec = tool.Spec;
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = spec.Purpose, TextWrapping = TextWrapping.Wrap });
            info.Children.Add(new TextBlock
            {
                Text = $"{spec.Id} v{spec.Version} · {spec.Risk switch { SlothBoss.Core.Agent.ToolRisk.Read => "reads only", SlothBoss.Core.Agent.ToolRisk.Create => "adds items", _ => "changes files" }}" +
                       (spec.UsesModel ? " · uses the local model" : "") + $" · checks: {spec.Verification}",
                TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            });
            row.Children.Add(info);
            var combo = new ComboBox { ItemsSource = labels, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center };
            combo.SelectedIndex = (int)Ws.Agent.Policy(spec.Id);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(combo, "Rule for " + spec.Purpose);
            combo.SelectionChanged += (_, _) => Ws.Agent.SetPolicy(spec.Id, (SlothBoss.Core.Agent.ToolPolicy)combo.SelectedIndex);
            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);
            PolicyList.Children.Add(row);
        }
    }

    private void OnAutoResumeToggled(object sender, RoutedEventArgs e)
    {
        if (!_loading) Ws.Settings.Set("agent.auto_resume", AutoResumeToggle.IsOn ? "true" : null);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => State.AiStatusChanged -= UpdateAi;

    private void RefreshWorkspace()
    {
        WorkspacePath.Text = Ws.Path;
        DefaultWorkspaceButton.IsEnabled = !string.Equals(Path.GetFullPath(Ws.Path), Path.GetFullPath(Workspace.DefaultPath()), StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateAi()
    {
        AiStatus.Text = State.AiState switch
        {
            AiState.Ready => $"Ready — {State.SelectedModel} via Ollama {State.OllamaVersion}",
            AiState.Checking => "Checking…",
            _ => State.AiMessage,
        };
        AiDot.Fill = (Brush)Application.Current.Resources[State.AiState switch
        {
            AiState.Ready => "SlothOkBrush",
            AiState.Checking => "TextFillColorTertiaryBrush",
            AiState.NoGemmaModel or AiState.ModelNotSelected => "SlothWarningBrush",
            _ => "SlothDangerBrush",
        }];
        AiDetail.Text = State.AiState switch
        {
            AiState.OllamaUnavailable => $"Nothing answered at {State.Client.Endpoint}. Install and start Ollama, then re-check. AI features are disabled until then; everything else works normally.",
            AiState.NoGemmaModel => "Install a Gemma model with the command below. SlothBoss lists only Gemma models and will not download anything for you.",
            AiState.ModelNotSelected => "Several Gemma models are installed — pick one.",
            AiState.Ready => "Answers, summaries and proposals are generated on this computer.",
            _ => "",
        };
        SetupExpander.IsExpanded = State.AiState is AiState.OllamaUnavailable or AiState.NoGemmaModel;

        _loading = true;
        // Rebuild the list only when the installed models changed: this runs again from the state change that
        // OnModelChanged triggers, and clearing a ComboBox inside its own SelectionChanged crashes WinUI.
        if (!ModelBox.Items.OfType<ComboBoxItem>().Select(i => (string)i.Tag).SequenceEqual(State.GemmaModels.Select(m => m.Name)))
        {
            ModelBox.Items.Clear();
            foreach (var m in State.GemmaModels)
                ModelBox.Items.Add(new ComboBoxItem { Content = $"{m.Name}   ({m.ParameterSize} {m.Quantization}, {m.SizeLabel})".Replace("( ", "(").Replace(" ,", ","), Tag = m.Name });
        }
        var selected = ModelBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == State.SelectedModel);
        if (!ReferenceEquals(ModelBox.SelectedItem, selected)) ModelBox.SelectedItem = selected;
        ModelBox.IsEnabled = State.GemmaModels.Count > 0;
        ModelBox.PlaceholderText = State.GemmaModels.Count == 0 ? "No Gemma models found" : "Choose a model";
        IgnoredModels.Text = State.IgnoredModelCount > 0 ? $"{State.IgnoredModelCount} non-Gemma model(s) installed in Ollama are not shown." : "";
        _loading = false;
    }

    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || ModelBox.SelectedItem is not ComboBoxItem { Tag: string name }) return;
        State.SelectModel(name);
    }

    private async void OnSaveAi(object sender, RoutedEventArgs e)
    {
        try
        {
            var endpoint = OllamaClient.ParseEndpoint(EndpointBox.Text);
            Ws.Settings.Set(SettingsStore.OllamaEndpoint, endpoint.ToString());
            var timeout = double.IsNaN(TimeoutBox.Value) ? 180 : (int)Math.Clamp(TimeoutBox.Value, 15, 1800);
            Ws.Settings.Set(SettingsStore.AiTimeoutSeconds, timeout.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await State.ReconfigureAiAsync();
        }
        catch (OllamaException ex) { App.Shell!.ShowError("Endpoint not saved", ex.Message); }
    }

    private async void OnRecheck(object sender, RoutedEventArgs e) => await State.RefreshAiAsync();

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var value = ThemeChoice.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" };
        Ws.Settings.Set(SettingsStore.Theme, value);
        App.Shell!.ApplyTheme();
    }

    // ---- workspace ------------------------------------------------------------------------

    private void OnShowFolder(object sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(Ws.Path));
        if (dir is not null && Directory.Exists(dir)) System.Diagnostics.Process.Start("explorer.exe", dir);
    }

    private string Generator => $"SlothBoss Windows {AppVersion}";

    private async void OnExportBackup(object sender, RoutedEventArgs e)
    {
        App.Shell!.FlushEditor();
        var path = await Pickers.SaveFileAsync(App.Shell, $"SlothBoss backup {DateTime.Now:yyyy-MM-dd}", "SlothBoss backup", ".json");
        if (path is null) return;
        try
        {
            BackupService.ExportToFile(Ws, path, Generator);
            var pages = Ws.Pages.List(includeArchived: true).Count;
            var tasks = Ws.Tasks.List().Count;
            App.Shell.ShowInfo($"Backup saved: {pages} page(s), {tasks} task(s).", InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.Shell.ShowError("Backup failed", ex.Message); }
    }

    private async Task<ValidatedBackup?> PickAndValidateAsync()
    {
        var path = await Pickers.OpenFileAsync(App.Shell!, ".json");
        if (path is null) return null;
        try { return BackupService.ValidateFile(path); }
        catch (SlothBossException ex) { App.Shell!.ShowError("This backup can't be restored", ex.Message); return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.Shell!.ShowError("Couldn't read the file", ex.Message); return null; }
    }

    private static string Describe(ValidatedBackup b) =>
        $"{b.Pages.Count} page(s) and {b.Tasks.Count} task(s)" + (b.ExportedAt.Length >= 10 ? $", exported {b.ExportedAt[..10]}" : "") +
        (b.Generator is null ? "" : $" by {b.Generator}") + "." +
        (b.Warnings.Count > 0 ? "\n\nNotes:\n• " + string.Join("\n• ", b.Warnings.Take(5)) : "");

    private async void OnRestoreReplace(object sender, RoutedEventArgs e)
    {
        var backup = await PickAndValidateAsync();
        if (backup is null) return;
        var current = $"{Ws.Pages.List(includeArchived: true).Count} page(s) and {Ws.Tasks.List().Count} task(s)";
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Replace this workspace?",
                $"The backup contains {Describe(backup)}\n\nThis replaces all {current} in the current workspace, and clears AI history. " +
                "A safety copy of the current workspace is saved next to it first.", "Replace", destructive: true))
            return;
        App.Shell!.FlushEditor();
        App.Shell.ShowHome();
        try
        {
            var snapshot = $"{Ws.Path}.before-restore-{DateTime.Now:yyyyMMdd-HHmmss}.db";
            Ws.SnapshotTo(snapshot);
            BackupService.RestoreReplacing(Ws, backup);
            App.Shell.RefreshSidebar(force: true);
            App.Shell.ShowInfo($"Restored {backup.Pages.Count} page(s) and {backup.Tasks.Count} task(s). Safety copy: {Path.GetFileName(snapshot)}", InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is SlothBossException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            App.Shell.ShowError("Restore failed — nothing was changed", ex.Message);
        }
    }

    private async void OnRestoreNew(object sender, RoutedEventArgs e)
    {
        var backup = await PickAndValidateAsync();
        if (backup is null) return;
        var path = await Pickers.SaveFileAsync(App.Shell!, "Restored workspace", "SlothBoss workspace", ".db");
        if (path is null) return;
        try
        {
            if (File.Exists(path))
            {
                // The save dialog may pre-create an empty file; never replace an existing non-empty file.
                if (new FileInfo(path).Length > 0) throw new SlothBossException("Choose a new file name; SlothBoss won't overwrite an existing file here.");
                File.Delete(path);
            }
            var ws = Workspace.Open(path);
            try { BackupService.RestoreReplacing(ws, backup); }
            catch { ws.Dispose(); throw; }
            await SwitchToAsync(ws, path, $"Restored {Describe(backup)} into a new workspace.");
        }
        catch (Exception ex) when (ex is SlothBossException or Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            App.Shell!.ShowError("Restore failed", ex.Message);
        }
    }

    private async void OnOpenWorkspace(object sender, RoutedEventArgs e)
    {
        var path = await Pickers.OpenFileAsync(App.Shell!, ".db");
        if (path is null) return;
        try { await SwitchToAsync(Workspace.Open(path), path, "Opened workspace."); }
        catch (Exception ex) when (ex is SlothBossException or Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            App.Shell!.ShowError("Couldn't open that workspace", ex.Message);
        }
    }

    private async void OnUseDefault(object sender, RoutedEventArgs e)
    {
        try { await SwitchToAsync(Workspace.Open(Workspace.DefaultPath()), Workspace.DefaultPath(), "Switched to the default workspace."); }
        catch (Exception ex) when (ex is SlothBossException or Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            App.Shell!.ShowError("Couldn't open the default workspace", ex.Message);
        }
    }

    private Task SwitchToAsync(Workspace ws, string path, string message)
    {
        App.Shell!.FlushEditor();
        App.Shell.ShowHome();
        State.SwitchWorkspace(ws);
        var config = AppConfig.Load();
        config.WorkspacePath = path;
        config.Save();
        App.Shell.RefreshSidebar(force: true);
        App.Shell.ApplyTheme();
        RefreshWorkspace();
        App.Shell.ShowInfo(message, InfoBarSeverity.Success);
        return Task.CompletedTask;
    }
}
