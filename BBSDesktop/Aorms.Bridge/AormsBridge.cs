// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Aorms.Bridge;

/// <summary>
/// Desktop connector: activate → syncToken, enqueue meta/artifacts, Flush to AORMS hub.
/// Canon: esti docs/esti/PORTAL-SYNC-BRIDGE.md · HUB-API 2026-08.
/// </summary>
public sealed class AormsBridge : IDisposable
{
    readonly BridgeOptions _opt;
    readonly FirmDb _db;
    readonly HttpClient _http;

    public AormsBridge(BridgeOptions options, string? firmDbPath = null)
    {
        _opt = options;
        _db = new FirmDb(firmDbPath ?? FirmDb.DefaultPath());
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        if (!string.IsNullOrWhiteSpace(_opt.DeviceId))
        {
            _db.UpsertSettings(
                _opt.DeviceId,
                licenseToken: null,
                syncToken: null,
                hubUrl: string.IsNullOrWhiteSpace(_opt.HubUrl) ? null : _opt.HubUrl.TrimEnd('/'),
                licenseApiUrl: string.IsNullOrWhiteSpace(_opt.LicenseApiUrl) ? null : _opt.LicenseApiUrl.TrimEnd('/'),
                licenceStatus: null);
        }
    }

    public FirmDb Db => _db;

    /// <summary>
    /// Import AORMS Connect session.json into this firm.db (C2 SSO).
    /// </summary>
    public bool TryImportConnectSession(string? sessionPath = null, bool overwrite = false) =>
        ConnectSession.TryApplyToFirmDb(
            _db,
            string.IsNullOrWhiteSpace(_opt.DeviceId) ? "device" : _opt.DeviceId,
            sessionPath ?? ConnectSession.ResolvePath(),
            overwrite);

    public HubConfigured HubConfigured()
    {
        var (sync, hub, _) = _db.ReadAuth();
        var hubUrl = string.IsNullOrWhiteSpace(hub) ? _opt.HubUrl.TrimEnd('/') : hub!;
        var lic = _opt.LicenseApiUrl.TrimEnd('/');
        var has = !string.IsNullOrWhiteSpace(sync);
        return new HubConfigured
        {
            HubUrl = hubUrl,
            LicenseApiUrl = lic,
            HasSyncToken = has,
            SyncReady = has && !string.IsNullOrWhiteSpace(hubUrl),
        };
    }

    /// <summary>Local licence status for Connect Licence Manager (C3).</summary>
    public LicenceSnapshot LicenceSnapshot()
    {
        var (installId, licenseToken, sync, hub, licenseApi, status, updatedAt) = _db.ReadLicenceRow();
        var hubUrl = string.IsNullOrWhiteSpace(hub) ? _opt.HubUrl.TrimEnd('/') : hub!;
        var licApi = string.IsNullOrWhiteSpace(licenseApi)
            ? _opt.LicenseApiUrl.TrimEnd('/')
            : licenseApi!.TrimEnd('/');
        var sessionPath = ConnectSession.DefaultPath();
        var inferredStatus = status;
        if (string.IsNullOrWhiteSpace(inferredStatus))
            inferredStatus = string.IsNullOrWhiteSpace(sync) ? "UNBOUND" : "ACTIVE";

        return new LicenceSnapshot
        {
            InstallId = string.IsNullOrWhiteSpace(installId) ? (_opt.DeviceId ?? "") : installId,
            LicenceStatus = inferredStatus,
            HasLicenseToken = !string.IsNullOrWhiteSpace(licenseToken),
            HasSyncToken = !string.IsNullOrWhiteSpace(sync),
            HubUrl = hubUrl,
            LicenseApiUrl = licApi,
            UpdatedAt = updatedAt,
            SessionFilePresent = File.Exists(sessionPath),
            SessionPath = sessionPath,
            FirmDbPath = _db.DbPath,
        };
    }

    public void ClearLocalLicence()
    {
        _db.ClearAuthTokens();
        var path = ConnectSession.DefaultPath();
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>POST /platform/v1/activate — persists licenseToken + syncToken.</summary>
    public async Task<ActivateResult> ActivateAsync(string licenseKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.LicenseApiUrl))
            throw new InvalidOperationException("LicenseApiUrl is not set.");
        if (string.IsNullOrWhiteSpace(_opt.ProductApiKey))
            throw new InvalidOperationException("ProductApiKey is not set.");
        if (string.IsNullOrWhiteSpace(_opt.DeviceId))
            throw new InvalidOperationException("DeviceId is not set.");

        var url = $"{_opt.LicenseApiUrl.TrimEnd('/')}/v1/activate";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.ProductApiKey);
        req.Content = JsonContent.Create(new ActivateRequest
        {
            LicenseKey = licenseKey,
            DeviceId = _opt.DeviceId,
            DeviceName = _opt.DeviceName,
        });

        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Activate failed {(int)res.StatusCode}: {body}");

        var parsed = JsonSerializer.Deserialize<ActivateResult>(body)
            ?? throw new InvalidOperationException("Activate returned empty body.");
        if (string.IsNullOrWhiteSpace(parsed.SyncToken))
            throw new InvalidOperationException("Activate response omitted syncToken (hub API 2026-08 required).");

        _db.UpsertSettings(
            _opt.DeviceId,
            parsed.LicenseToken,
            parsed.SyncToken,
            string.IsNullOrWhiteSpace(_opt.HubUrl) ? null : _opt.HubUrl.TrimEnd('/'),
            string.IsNullOrWhiteSpace(_opt.LicenseApiUrl) ? null : _opt.LicenseApiUrl.TrimEnd('/'),
            "ACTIVE");
        return parsed;
    }

    public long EnqueueMeta(string entity, string entityId, object payload, string op = "UPSERT") =>
        _db.EnqueueMeta(entity, entityId, payload, op);

    public long EnqueueArtifact(string entity, string entityId, object payload, string? contentHash = null, string? storageKey = null) =>
        _db.EnqueueArtifact(entity, entityId, payload, contentHash, storageKey);

    /// <summary>Pending meta/artifact outbox counts for Connect DB connector UI.</summary>
    public OutboxCounts OutboxCounts() => _db.CountPendingOutbox();

    /// <summary>
    /// Drain PENDING outboxes to hub. Returns skipped reason when not sync-ready.
    /// Meta POST /api/sync/meta · Artifact POST /api/sync/ingest (JSON envelope; binary upload wave 2).
    /// </summary>
    public async Task<FlushResult> FlushAsync(CancellationToken ct = default)
    {
        var cfg = HubConfigured();
        if (!cfg.SyncReady)
        {
            var reason = !cfg.HasSyncToken ? "missing_sync_token" :
                string.IsNullOrWhiteSpace(cfg.HubUrl) ? "hub_unconfigured" : "sync_disabled";
            return new FlushResult { SkippedReason = reason };
        }

        var (syncToken, hubUrl, _) = _db.ReadAuth();
        var hub = (hubUrl ?? cfg.HubUrl).TrimEnd('/');
        var metaSent = 0;
        var artSent = 0;

        foreach (var row in _db.PendingMeta())
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{hub}/api/sync/meta");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", syncToken);
                // Envelope aligned with hub MetaEventBody — fields refined as wire freezes.
                var envelope = new Dictionary<string, object?>
                {
                    ["stream"] = "firm",
                    ["entity"] = row.Entity,
                    ["entityId"] = row.EntityId,
                    ["op"] = row.Op,
                    ["patch"] = JsonSerializer.Deserialize<JsonElement>(row.PayloadJson),
                    ["updatedAt"] = DateTime.UtcNow.ToString("O"),
                    ["conflict"] = "lwwField",
                };
                req.Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json");
                using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    var err = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    _db.MarkMeta(row.Id, "FAILED", $"{(int)res.StatusCode}: {err}");
                    continue;
                }
                _db.MarkMeta(row.Id, "SYNCED");
                metaSent++;
            }
            catch (Exception ex)
            {
                _db.MarkMeta(row.Id, "FAILED", ex.Message);
            }
        }

        foreach (var row in _db.PendingArtifacts())
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{hub}/api/sync/ingest");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", syncToken);
                var envelope = new Dictionary<string, object?>
                {
                    ["entity"] = row.Entity,
                    ["entityId"] = row.EntityId,
                    ["op"] = "UPSERT",
                    ["payload"] = JsonSerializer.Deserialize<JsonElement>(row.PayloadJson),
                    ["fileKeys"] = Array.Empty<string>(),
                    ["contentHash"] = row.Hash,
                };
                req.Content = new StringContent(JsonSerializer.Serialize(envelope), Encoding.UTF8, "application/json");
                using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
                if (!res.IsSuccessStatusCode)
                {
                    var err = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    _db.MarkArtifact(row.Id, "FAILED", $"{(int)res.StatusCode}: {err}");
                    continue;
                }
                _db.MarkArtifact(row.Id, "SYNCED");
                artSent++;
            }
            catch (Exception ex)
            {
                _db.MarkArtifact(row.Id, "FAILED", ex.Message);
            }
        }

        return new FlushResult { MetaSent = metaSent, ArtifactsSent = artSent };
    }

    /// <summary>POST /api/ops/tasks — suite Mongo ops (practice manager Tasks module).</summary>
    public async Task PublishOpsTaskAsync(
        string projectId,
        string taskId,
        string title,
        string status,
        CancellationToken ct = default)
    {
        var cfg = HubConfigured();
        if (!cfg.SyncReady)
            throw new InvalidOperationException(cfg.HasSyncToken ? "hub_unconfigured" : "missing_sync_token");

        var (syncToken, hubUrl, _) = _db.ReadAuth();
        var hub = (hubUrl ?? cfg.HubUrl).TrimEnd('/');
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{hub}/api/ops/tasks");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", syncToken);
        var body = new Dictionary<string, object?>
        {
            ["projectId"] = projectId,
            ["taskId"] = taskId,
            ["title"] = title,
            ["status"] = status,
            ["updatedAt"] = DateTime.UtcNow.ToString("O"),
        };
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"PublishOpsTask failed {(int)res.StatusCode}: {text}");

        _db.UpsertLocalTask(taskId, projectId, title, status, "PUBLISHED");
    }

    public void Dispose()
    {
        _http.Dispose();
        _db.Dispose();
    }
}

