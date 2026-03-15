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
                name=excluded.name, alias=excluded.alias,
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

    public void DeleteCamerasForNvr(int nvrId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM cameras WHERE nvr_id = @nvrId";
        cmd.Parameters.AddWithValue("@nvrId", nvrId);
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

    public void Dispose()
    {
        _conn.Dispose();
    }
}
