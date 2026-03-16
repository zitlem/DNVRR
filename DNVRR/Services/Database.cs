using System.IO;
using Microsoft.Data.Sqlite;
using DNVRR.Models;

namespace DNVRR.Services;

/// <summary>
/// SQLite database for NVRs and cameras. Compatible with NVRR schema.
/// </summary>
public class Database : IDisposable
{
    private readonly SqliteConnection _conn;

    public Database(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();

        Execute("PRAGMA journal_mode=WAL");
        Execute("PRAGMA foreign_keys=ON");
        InitSchema();
    }

    private void InitSchema()
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS nvrs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                alias TEXT NOT NULL DEFAULT '',
                ip TEXT NOT NULL UNIQUE,
                username TEXT NOT NULL,
                password TEXT NOT NULL,
                port INTEGER NOT NULL DEFAULT 80,
                sdk_port INTEGER NOT NULL DEFAULT 8000,
                channels INTEGER NOT NULL DEFAULT 0,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS cameras (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                nvr_id INTEGER NOT NULL,
                channel INTEGER NOT NULL,
                name TEXT NOT NULL,
                rtsp_url TEXT NOT NULL,
                enabled INTEGER NOT NULL DEFAULT 1,
                ptz_enabled INTEGER NOT NULL DEFAULT 0,
                connected INTEGER NOT NULL DEFAULT 1,
                onvif_host TEXT,
                onvif_port INTEGER DEFAULT 80,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                FOREIGN KEY (nvr_id) REFERENCES nvrs(id) ON DELETE CASCADE,
                UNIQUE(nvr_id, channel)
            );

            CREATE TABLE IF NOT EXISTS window_state (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                view_id INTEGER,
                x REAL NOT NULL DEFAULT 0,
                y REAL NOT NULL DEFAULT 0,
                width REAL NOT NULL DEFAULT 1280,
                height REAL NOT NULL DEFAULT 720,
                is_maximized INTEGER NOT NULL DEFAULT 1,
                left_sidebar INTEGER NOT NULL DEFAULT 1,
                right_sidebar INTEGER NOT NULL DEFAULT 1,
                sort_order INTEGER NOT NULL DEFAULT 0,
                FOREIGN KEY (view_id) REFERENCES views(id) ON DELETE SET NULL
            );

            CREATE TABLE IF NOT EXISTS views (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL,
                slug TEXT NOT NULL UNIQUE,
                cols INTEGER NOT NULL DEFAULT 4,
                rows INTEGER NOT NULL DEFAULT 3,
                grid TEXT NOT NULL DEFAULT '[]',
                sort_order INTEGER NOT NULL DEFAULT 0,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );
        """);

        // Migrations for existing databases
        try { Execute("ALTER TABLE window_state ADD COLUMN left_sidebar INTEGER NOT NULL DEFAULT 1"); } catch { }
        try { Execute("ALTER TABLE window_state ADD COLUMN right_sidebar INTEGER NOT NULL DEFAULT 1"); } catch { }
    }

    private void Execute(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    // --- NVR Operations ---

    public List<NvrInfo> GetNvrs()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM nvrs ORDER BY id";
        using var reader = cmd.ExecuteReader();
        var list = new List<NvrInfo>();
        while (reader.Read())
            list.Add(ReadNvr(reader));
        return list;
    }

    public NvrInfo? GetNvr(int id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM nvrs WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadNvr(reader) : null;
    }

    public int AddNvr(NvrInfo nvr)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO nvrs (name, alias, ip, username, password, port, sdk_port, channels)
            VALUES (@name, @alias, @ip, @user, @pass, @port, @sdkPort, @channels)
            ON CONFLICT(ip) DO UPDATE SET
                name=excluded.name,
                username=excluded.username, password=excluded.password,
                port=excluded.port, sdk_port=excluded.sdk_port, channels=excluded.channels
            RETURNING id
        """;
        cmd.Parameters.AddWithValue("@name", nvr.Name);
        cmd.Parameters.AddWithValue("@alias", nvr.Alias);
        cmd.Parameters.AddWithValue("@ip", nvr.Ip);
        cmd.Parameters.AddWithValue("@user", nvr.Username);
        cmd.Parameters.AddWithValue("@pass", nvr.Password);
        cmd.Parameters.AddWithValue("@port", nvr.Port);
        cmd.Parameters.AddWithValue("@sdkPort", nvr.SdkPort);
        cmd.Parameters.AddWithValue("@channels", nvr.Channels);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void UpdateNvrAlias(int id, string alias)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE nvrs SET alias = @alias WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@alias", alias);
        cmd.ExecuteNonQuery();
    }

    public void DeleteNvr(int id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM nvrs WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    // --- Camera Operations ---

    public List<CameraInfo> GetCameras(int? nvrId = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = nvrId.HasValue
            ? "SELECT * FROM cameras WHERE nvr_id = @nvrId ORDER BY channel"
            : "SELECT * FROM cameras ORDER BY nvr_id, channel";
        if (nvrId.HasValue)
            cmd.Parameters.AddWithValue("@nvrId", nvrId.Value);
        using var reader = cmd.ExecuteReader();
        var list = new List<CameraInfo>();
        while (reader.Read())
            list.Add(ReadCamera(reader));
        return list;
    }

    public int AddCamera(CameraInfo cam)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO cameras (nvr_id, channel, name, rtsp_url, enabled, ptz_enabled, connected, onvif_host, onvif_port)
            VALUES (@nvrId, @ch, @name, @rtsp, @enabled, @ptz, @connected, @onvifHost, @onvifPort)
            ON CONFLICT(nvr_id, channel) DO UPDATE SET
                name=excluded.name, rtsp_url=excluded.rtsp_url,
                enabled=excluded.enabled, connected=excluded.connected
            RETURNING id
        """;
        cmd.Parameters.AddWithValue("@nvrId", cam.NvrId);
        cmd.Parameters.AddWithValue("@ch", cam.Channel);
        cmd.Parameters.AddWithValue("@name", cam.Name);
        cmd.Parameters.AddWithValue("@rtsp", cam.RtspUrl);
        cmd.Parameters.AddWithValue("@enabled", cam.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@ptz", cam.PtzEnabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@connected", cam.Connected ? 1 : 0);
        cmd.Parameters.AddWithValue("@onvifHost", (object?)cam.OnvifHost ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@onvifPort", cam.OnvifPort);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void UpdateCamera(int id, bool enabled, bool ptzEnabled)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE cameras SET enabled = @enabled, ptz_enabled = @ptz WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@enabled", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@ptz", ptzEnabled ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void UpdateCameraConnected(int id, bool connected)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE cameras SET connected = @c WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@c", connected ? 1 : 0);
        cmd.ExecuteNonQuery();
    }

    public void DeleteCamerasForNvr(int nvrId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM cameras WHERE nvr_id = @nvrId";
        cmd.Parameters.AddWithValue("@nvrId", nvrId);
        cmd.ExecuteNonQuery();
    }

    // --- View Operations ---

    public List<ViewInfo> GetViews()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM views ORDER BY sort_order, id";
        using var reader = cmd.ExecuteReader();
        var list = new List<ViewInfo>();
        while (reader.Read())
            list.Add(ReadView(reader));
        return list;
    }

    public int AddView(ViewInfo view)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO views (name, slug, cols, rows, grid, sort_order)
            VALUES (@name, @slug, @cols, @rows, @grid, @sort)
            ON CONFLICT(slug) DO UPDATE SET
                name=excluded.name, cols=excluded.cols, rows=excluded.rows,
                grid=excluded.grid, sort_order=excluded.sort_order
            RETURNING id
        """;
        cmd.Parameters.AddWithValue("@name", view.Name);
        cmd.Parameters.AddWithValue("@slug", view.Slug);
        cmd.Parameters.AddWithValue("@cols", view.Cols);
        cmd.Parameters.AddWithValue("@rows", view.Rows);
        cmd.Parameters.AddWithValue("@grid", view.Grid);
        cmd.Parameters.AddWithValue("@sort", view.SortOrder);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public void UpdateView(ViewInfo view)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE views SET name=@name, slug=@slug, cols=@cols, rows=@rows, grid=@grid, sort_order=@sort WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", view.Id);
        cmd.Parameters.AddWithValue("@name", view.Name);
        cmd.Parameters.AddWithValue("@slug", view.Slug);
        cmd.Parameters.AddWithValue("@cols", view.Cols);
        cmd.Parameters.AddWithValue("@rows", view.Rows);
        cmd.Parameters.AddWithValue("@grid", view.Grid);
        cmd.Parameters.AddWithValue("@sort", view.SortOrder);
        cmd.ExecuteNonQuery();
    }

    public void DeleteView(int id)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM views WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    // --- Helpers ---

    private static NvrInfo ReadNvr(SqliteDataReader r) => new()
    {
        Id = r.GetInt32(r.GetOrdinal("id")),
        Name = r.GetString(r.GetOrdinal("name")),
        Alias = r.GetString(r.GetOrdinal("alias")),
        Ip = r.GetString(r.GetOrdinal("ip")),
        Username = r.GetString(r.GetOrdinal("username")),
        Password = r.GetString(r.GetOrdinal("password")),
        Port = r.GetInt32(r.GetOrdinal("port")),
        SdkPort = r.GetInt32(r.GetOrdinal("sdk_port")),
        Channels = r.GetInt32(r.GetOrdinal("channels")),
    };

    private static CameraInfo ReadCamera(SqliteDataReader r) => new()
    {
        Id = r.GetInt32(r.GetOrdinal("id")),
        NvrId = r.GetInt32(r.GetOrdinal("nvr_id")),
        Channel = r.GetInt32(r.GetOrdinal("channel")),
        Name = r.GetString(r.GetOrdinal("name")),
        RtspUrl = r.GetString(r.GetOrdinal("rtsp_url")),
        Enabled = r.GetInt32(r.GetOrdinal("enabled")) == 1,
        PtzEnabled = r.GetInt32(r.GetOrdinal("ptz_enabled")) == 1,
        Connected = r.GetInt32(r.GetOrdinal("connected")) == 1,
        OnvifHost = r.IsDBNull(r.GetOrdinal("onvif_host")) ? null : r.GetString(r.GetOrdinal("onvif_host")),
        OnvifPort = r.GetInt32(r.GetOrdinal("onvif_port")),
    };

    private static ViewInfo ReadView(SqliteDataReader r) => new()
    {
        Id = r.GetInt32(r.GetOrdinal("id")),
        Name = r.GetString(r.GetOrdinal("name")),
        Slug = r.GetString(r.GetOrdinal("slug")),
        Cols = r.GetInt32(r.GetOrdinal("cols")),
        Rows = r.GetInt32(r.GetOrdinal("rows")),
        Grid = r.GetString(r.GetOrdinal("grid")),
        SortOrder = r.GetInt32(r.GetOrdinal("sort_order")),
    };

    // --- Window State Operations ---

    public List<SavedWindowState> GetWindowStates()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM window_state ORDER BY sort_order";
        using var reader = cmd.ExecuteReader();
        var list = new List<SavedWindowState>();
        while (reader.Read())
            list.Add(ReadSavedWindowState(reader));
        return list;
    }

    public void SaveWindowStates(List<SavedWindowState> states)
    {
        Execute("DELETE FROM window_state");
        for (int i = 0; i < states.Count; i++)
        {
            var s = states[i];
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO window_state (view_id, x, y, width, height, is_maximized, left_sidebar, right_sidebar, sort_order)
                VALUES (@viewId, @x, @y, @w, @h, @max, @left, @right, @sort)
            """;
            cmd.Parameters.AddWithValue("@viewId", (object?)s.ViewId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@x", s.X);
            cmd.Parameters.AddWithValue("@y", s.Y);
            cmd.Parameters.AddWithValue("@w", s.Width);
            cmd.Parameters.AddWithValue("@h", s.Height);
            cmd.Parameters.AddWithValue("@max", s.IsMaximized ? 1 : 0);
            cmd.Parameters.AddWithValue("@left", s.LeftSidebarOpen ? 1 : 0);
            cmd.Parameters.AddWithValue("@right", s.RightSidebarOpen ? 1 : 0);
            cmd.Parameters.AddWithValue("@sort", i);
            cmd.ExecuteNonQuery();
        }
    }

    private static SavedWindowState ReadSavedWindowState(SqliteDataReader r) => new()
    {
        Id = r.GetInt32(r.GetOrdinal("id")),
        ViewId = r.IsDBNull(r.GetOrdinal("view_id")) ? null : r.GetInt32(r.GetOrdinal("view_id")),
        X = r.GetDouble(r.GetOrdinal("x")),
        Y = r.GetDouble(r.GetOrdinal("y")),
        Width = r.GetDouble(r.GetOrdinal("width")),
        Height = r.GetDouble(r.GetOrdinal("height")),
        IsMaximized = r.GetInt32(r.GetOrdinal("is_maximized")) == 1,
        LeftSidebarOpen = r.GetInt32(r.GetOrdinal("left_sidebar")) == 1,
        RightSidebarOpen = r.GetInt32(r.GetOrdinal("right_sidebar")) == 1,
        SortOrder = r.GetInt32(r.GetOrdinal("sort_order")),
    };

    public void Dispose()
    {
        _conn.Dispose();
    }
}
