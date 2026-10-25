using Microsoft.Data.Sqlite;
using SlothBoss.Core.Actions;

namespace SlothBoss.Core.Storage;

/// <summary>
/// An open SlothBoss workspace (one SQLite file). All database access goes through
/// <see cref="Read{T}"/> / <see cref="Write{T}"/>, which serialize access to the single
/// connection so UI autosave and background AI retrieval can't interleave statements.
/// </summary>
public sealed class Workspace : IDisposable
{
    public const int SchemaVersion = 2;

    private readonly SqliteConnection _conn;
    private readonly Lock _gate = new();

    public string Path { get; }
    public PageStore Pages { get; }
    public TaskStore Tasks { get; }
    public SettingsStore Settings { get; }
    public SearchService Search { get; }
    public ActionStore Actions { get; }
    public Agent.AgentStore Agent { get; }

    /// <summary>Raised after any committed write (used by the UI to refresh views).</summary>
    public event EventHandler<WorkspaceChange>? Changed;

    private Workspace(string path, SqliteConnection conn)
    {
        Path = path;
        _conn = conn;
        Pages = new PageStore(this);
        Tasks = new TaskStore(this);
        Settings = new SettingsStore(this);
        Search = new SearchService(this);
        Actions = new ActionStore(this);
        Agent = new Agent.AgentStore(this);
    }

    /// <summary>Default location: %LOCALAPPDATA%\SlothBoss\workspace.db.</summary>
    public static string DefaultPath() =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SlothBoss", "workspace.db");

    /// <summary>Opens (creating if needed) and migrates the workspace. Use ":memory:" for tests.</summary>
    public static Workspace Open(string path)
    {
        if (path != ":memory:")
        {
            var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        conn.Open();
        try
        {
            Exec(conn, "PRAGMA foreign_keys = ON;");
            Exec(conn, "PRAGMA busy_timeout = 5000;");
            if (path != ":memory:") Exec(conn, "PRAGMA journal_mode = WAL;");
            Migrate(conn);
            return new Workspace(path, conn);
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void Migrate(SqliteConnection conn)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText = "SELECT sqlite_compileoption_used('ENABLE_FTS5');";
            if (Convert.ToInt64(check.ExecuteScalar()) != 1)
                throw new SlothBossException("This SQLite build lacks FTS5, which SlothBoss requires for search.");
        }

        long current;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA user_version;";
            current = Convert.ToInt64(cmd.ExecuteScalar());
        }
        if (current > SchemaVersion)
            throw new SlothBossException(
                $"This workspace uses schema version {current}, which is newer than this app supports ({SchemaVersion}). " +
                "Update SlothBoss to open it. The file was not modified.");

        foreach (var name in Resources.Names("schema."))
        {
            // schema.001_initial.sql -> 1
            var version = int.Parse(name.AsSpan(7, 3));
            if (version <= current) continue;
            using var tx = conn.BeginTransaction();
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = Resources.Read(name);
                cmd.ExecuteNonQuery();
            }
            using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = $"PRAGMA user_version = {version};";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }

    public T Read<T>(Func<SqliteConnection, T> work)
    {
        lock (_gate) return work(_conn);
    }

    /// <summary>Runs <paramref name="work"/> in a transaction; commits only if it returns normally.</summary>
    public T Write<T>(WorkspaceChange change, Func<SqliteConnection, SqliteTransaction, T> work)
    {
        T result;
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            result = work(_conn, tx);
            tx.Commit();
        }
        Changed?.Invoke(this, change);
        return result;
    }

    public void Write(WorkspaceChange change, Action<SqliteConnection, SqliteTransaction> work) =>
        Write<bool>(change, (c, t) => { work(c, t); return true; });

    /// <summary>Writes a consistent snapshot of the database to <paramref name="destination"/> (must not exist).</summary>
    public void SnapshotTo(string destination)
    {
        if (File.Exists(destination)) throw new SlothBossException($"Refusing to overwrite existing file {destination}.");
        Read(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "VACUUM INTO $path;";
            cmd.Parameters.AddWithValue("$path", destination);
            cmd.ExecuteNonQuery();
            return true;
        });
    }

    public void Dispose()
    {
        lock (_gate) _conn.Dispose();
    }
}

[Flags]
public enum WorkspaceChange { None = 0, Pages = 1, Tasks = 2, Settings = 4, Actions = 8, Agent = 16, All = 31 }

internal static class SqliteExtensions
{
    public static SqliteCommand Cmd(this SqliteConnection conn, string sql, SqliteTransaction? tx = null, params (string Name, object? Value)[] args)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    public static int Exec(this SqliteConnection conn, string sql, SqliteTransaction? tx = null, params (string, object?)[] args)
    {
        using var cmd = conn.Cmd(sql, tx, args);
        return cmd.ExecuteNonQuery();
    }

    public static object? Scalar(this SqliteConnection conn, string sql, SqliteTransaction? tx = null, params (string, object?)[] args)
    {
        using var cmd = conn.Cmd(sql, tx, args);
        var v = cmd.ExecuteScalar();
        return v is DBNull ? null : v;
    }

    public static List<T> Query<T>(this SqliteConnection conn, string sql, Func<SqliteDataReader, T> map, SqliteTransaction? tx = null, params (string, object?)[] args)
    {
        using var cmd = conn.Cmd(sql, tx, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read()) list.Add(map(r));
        return list;
    }

    public static string? NullableString(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
