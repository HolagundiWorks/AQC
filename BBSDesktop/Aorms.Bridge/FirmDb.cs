// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Aorms.Bridge;

/// <summary>
/// Firm-level SQLite for licence tokens + sync outboxes (not the per-project .aqcdb).
/// Path: %LocalAppData%\{appName}\firm.db
/// </summary>
public sealed class FirmDb : IDisposable
{
    private readonly SqliteConnection _con;

    public string DbPath { get; }

    public FirmDb(string dbPath)
    {
        DbPath = Path.GetFullPath(dbPath);
        var dir = Path.GetDirectoryName(DbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        SQLitePCL.Batteries_V2.Init();
        _con = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        _con.Open();
        EnsureSchema();
    }

    public static string DefaultPath(string appFolderName = "AQCCore") =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            appFolderName,
            "firm.db");

    void EnsureSchema()
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS org_settings(
              id INTEGER PRIMARY KEY CHECK (id = 1),
              install_id TEXT NOT NULL,
              license_token TEXT,
              sync_token TEXT,
              hub_url TEXT,
              license_api_url TEXT,
              licence_status TEXT,
              updated_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS meta_outbox(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              entity TEXT NOT NULL,
              entity_id TEXT NOT NULL,
              op TEXT NOT NULL DEFAULT 'UPSERT',
              payload_json TEXT NOT NULL,
              state TEXT NOT NULL DEFAULT 'PENDING',
              error TEXT,
              created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS artifact_outbox(
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              entity TEXT NOT NULL,
              entity_id TEXT NOT NULL,
              content_hash TEXT,
              storage_key TEXT,
              payload_json TEXT NOT NULL,
              state TEXT NOT NULL DEFAULT 'PENDING',
              error TEXT,
              created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS meta_cursor(
              stream TEXT PRIMARY KEY,
              last_seq INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS local_tasks(
              task_id TEXT PRIMARY KEY,
              project_id TEXT NOT NULL,
              title TEXT NOT NULL,
              status TEXT NOT NULL,
              publish_state TEXT NOT NULL DEFAULT 'LOCAL',
              updated_at TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
    }

    public void UpsertSettings(
        string installId,
        string? licenseToken,
        string? syncToken,
        string? hubUrl,
        string? licenseApiUrl,
        string? licenceStatus)
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = """
            INSERT INTO org_settings(id, install_id, license_token, sync_token, hub_url, license_api_url, licence_status, updated_at)
            VALUES(1, $i, $lt, $st, $h, $la, $ls, $u)
            ON CONFLICT(id) DO UPDATE SET
              install_id=excluded.install_id,
              license_token=COALESCE(excluded.license_token, org_settings.license_token),
              sync_token=COALESCE(excluded.sync_token, org_settings.sync_token),
              hub_url=COALESCE(excluded.hub_url, org_settings.hub_url),
              license_api_url=COALESCE(excluded.license_api_url, org_settings.license_api_url),
              licence_status=COALESCE(excluded.licence_status, org_settings.licence_status),
              updated_at=excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$i", installId);
        cmd.Parameters.AddWithValue("$lt", (object?)licenseToken ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$st", (object?)syncToken ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$h", (object?)hubUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$la", (object?)licenseApiUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ls", (object?)licenceStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public (string? SyncToken, string? HubUrl, string InstallId) ReadAuth()
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = "SELECT sync_token, hub_url, install_id FROM org_settings WHERE id=1";
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (null, null, "");
        return (
            r.IsDBNull(0) ? null : r.GetString(0),
            r.IsDBNull(1) ? null : r.GetString(1),
            r.IsDBNull(2) ? "" : r.GetString(2));
    }

    /// <summary>Full org_settings row for Connect Licence Manager (C3).</summary>
    public (
        string InstallId,
        string? LicenseToken,
        string? SyncToken,
        string? HubUrl,
        string? LicenseApiUrl,
        string? LicenceStatus,
        string? UpdatedAt
    ) ReadLicenceRow()
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = """
            SELECT install_id, license_token, sync_token, hub_url, license_api_url, licence_status, updated_at
            FROM org_settings WHERE id=1
            """;
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return ("", null, null, null, null, null, null);
        return (
            r.IsDBNull(0) ? "" : r.GetString(0),
            r.IsDBNull(1) ? null : r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2),
            r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5),
            r.IsDBNull(6) ? null : r.GetString(6));
    }

    /// <summary>Clear hub tokens locally (does not call License Manager revoke).</summary>
    public void ClearAuthTokens()
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = """
            UPDATE org_settings SET
              license_token=NULL,
              sync_token=NULL,
              licence_status='UNBOUND',
              updated_at=$u
            WHERE id=1
            """;
        cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public long EnqueueMeta(string entity, string entityId, object payload, string op = "UPSERT")
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = """
            INSERT INTO meta_outbox(entity, entity_id, op, payload_json, state, created_at)
            VALUES($e, $id, $op, $p, 'PENDING', $t);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$e", entity);
        cmd.Parameters.AddWithValue("$id", entityId);
        cmd.Parameters.AddWithValue("$op", op);
        cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(payload));
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public long EnqueueArtifact(string entity, string entityId, object payload, string? contentHash = null, string? storageKey = null)
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = """
            INSERT INTO artifact_outbox(entity, entity_id, content_hash, storage_key, payload_json, state, created_at)
            VALUES($e, $id, $h, $k, $p, 'PENDING', $t);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$e", entity);
        cmd.Parameters.AddWithValue("$id", entityId);
        cmd.Parameters.AddWithValue("$h", (object?)contentHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$k", (object?)storageKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$p", JsonSerializer.Serialize(payload));
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
        return (long)(cmd.ExecuteScalar() ?? 0L);
    }

    public OutboxCounts CountPendingOutbox()
    {
        using var meta = _con.CreateCommand();
        meta.CommandText = "SELECT COUNT(*) FROM meta_outbox WHERE state='PENDING'";
        var pendingMeta = Convert.ToInt32(meta.ExecuteScalar() ?? 0);
        using var art = _con.CreateCommand();
        art.CommandText = "SELECT COUNT(*) FROM artifact_outbox WHERE state='PENDING'";
        var pendingArt = Convert.ToInt32(art.ExecuteScalar() ?? 0);
        return new OutboxCounts { PendingMeta = pendingMeta, PendingArtifacts = pendingArt };
    }

    public List<(long Id, string Entity, string EntityId, string Op, string PayloadJson)> PendingMeta(int limit = 50)
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = "SELECT id, entity, entity_id, op, payload_json FROM meta_outbox WHERE state='PENDING' ORDER BY id LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        var list = new List<(long, string, string, string, string)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return list;
    }

    public List<(long Id, string Entity, string EntityId, string PayloadJson, string? Hash)> PendingArtifacts(int limit = 20)
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = "SELECT id, entity, entity_id, payload_json, content_hash FROM artifact_outbox WHERE state='PENDING' ORDER BY id LIMIT $n";
        cmd.Parameters.AddWithValue("$n", limit);
        var list = new List<(long, string, string, string, string?)>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return list;
    }

    public void MarkMeta(long id, string state, string? error = null)
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = "UPDATE meta_outbox SET state=$s, error=$e WHERE id=$id";
        cmd.Parameters.AddWithValue("$s", state);
        cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void MarkArtifact(long id, string state, string? error = null)
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = "UPDATE artifact_outbox SET state=$s, error=$e WHERE id=$id";
        cmd.Parameters.AddWithValue("$s", state);
        cmd.Parameters.AddWithValue("$e", (object?)error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void UpsertLocalTask(string taskId, string projectId, string title, string status, string publishState)
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = """
            INSERT INTO local_tasks(task_id, project_id, title, status, publish_state, updated_at)
            VALUES($id,$p,$t,$s,$ps,$u)
            ON CONFLICT(task_id) DO UPDATE SET
              project_id=excluded.project_id,
              title=excluded.title,
              status=excluded.status,
              publish_state=excluded.publish_state,
              updated_at=excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$id", taskId);
        cmd.Parameters.AddWithValue("$p", projectId);
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$s", status);
        cmd.Parameters.AddWithValue("$ps", publishState);
        cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string TaskId, string ProjectId, string Title, string Status, string PublishState)> ListLocalTasks()
    {
        using var cmd = _con.CreateCommand();
        cmd.CommandText = "SELECT task_id, project_id, title, status, publish_state FROM local_tasks ORDER BY updated_at DESC LIMIT 100";
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string, string, string, string)>();
        while (r.Read())
            list.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return list;
    }

    public void Dispose() => _con.Dispose();
}
