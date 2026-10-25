using Microsoft.UI.Xaml;
using SlothBoss.Core;
using SlothBoss.Core.Storage;

namespace SlothBoss.App;

public partial class App : Application
{
    public static MainWindow? Shell { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // Surface unexpected errors instead of silently crashing; the workspace is transactional,
            // so a failed operation never leaves partial writes behind.
            e.Handled = true;
            Shell?.ShowError("Something went wrong", e.Exception.Message);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var argv = Environment.GetCommandLineArgs();
        var selfTest = Array.IndexOf(argv, "--selftest-editor");
        if (selfTest >= 0 && argv.Length > selfTest + 2)
        {
            new Editor.EditorSelfTest(argv[selfTest + 1], argv[selfTest + 2]).Activate();
            return;
        }

        // --workspace <file> opens a specific workspace for this session only (demos, testing).
        var wsArg = Array.IndexOf(argv, "--workspace");
        var path = wsArg >= 0 && argv.Length > wsArg + 1 ? Path.GetFullPath(argv[wsArg + 1]) : AppConfig.Load().WorkspacePath;

        var online = Array.IndexOf(argv, "--selftest-online");
        if (online >= 0 && argv.Length > online + 1)
        {
            _ = OnlineSelfTest.RunAsync(argv[online + 1]).ContinueWith(_ => Exit(), TaskScheduler.FromCurrentSynchronizationContext());
            return;
        }

        Workspace ws;
        try
        {
            ws = Workspace.Open(path);
        }
        catch (Exception e) when (e is SlothBossException or Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        {
            var failed = new StartupErrorWindow(e.Message);
            failed.Activate();
            return;
        }

        // --practice-folder <parent>: create a synthetic messy folder there and approve it (demos, testing).
        var practice = Array.IndexOf(argv, "--practice-folder");
        if (practice >= 0 && argv.Length > practice + 1)
        {
            try { ws.Agent.ApproveFolder(SlothBoss.Core.Agent.PracticeFolder.Create(Path.GetFullPath(argv[practice + 1]))); }
            catch (Exception e) when (e is SlothBossException or IOException) { System.Diagnostics.Debug.WriteLine(e); }
        }

        var window = new MainWindow();
        AppState.Initialize(ws, window.DispatcherQueue);
        Shell = window;
        window.Start();
        window.Activate();
    }
}
