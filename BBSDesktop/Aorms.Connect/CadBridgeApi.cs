// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aorms.Connect;

/// <summary>One published CAD take-off (<c>cad_bridge</c> version): the <c>hcw-aqc-bridge</c> v1 file AAD measured, held by the portal.</summary>
public sealed record CadBridgeVersion(string Id, int Version, string? CreatedAt, bool HasFile, JsonObject Summary);

/// <summary>
/// The CAD → portal → AQC hand-off (docs/esti/AORMS-SUITE-SIGNIN.md). The file contract is unchanged (docs/bridge/): this only moves the same
/// JSON through the portal. AQC feeds <see cref="DownloadLatestAsync"/> to its existing importer (idempotent by source / source_ref).
/// </summary>
public sealed class CadBridgeApi
{
    readonly ConnectClient _c;
    readonly VersionsApi _versions;
    public CadBridgeApi(ConnectClient client) { _c = client; _versions = new VersionsApi(client); }

    /// <summary>Newest first.</summary>
    public async Task<List<CadBridgeVersion>> ListAsync(string aqcId, CancellationToken ct = default)
    {
        var j = await _c.GetAsync<JsonElement>($"api/aqc/v1/projects/{Uri.EscapeDataString(aqcId)}/versions?kind=cad_bridge", ct);
        var list = new List<CadBridgeVersion>();
        foreach (var v in j.GetProperty("versions").EnumerateArray())
        {
            var summary = v.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.Object ? (JsonObject)JsonNode.Parse(s.GetRawText())! : new JsonObject();
            list.Add(new CadBridgeVersion(v.GetProperty("id").GetString()!, v.TryGetProperty("version", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0,
                v.TryGetProperty("created_at", out var c) ? c.GetString() : null, v.TryGetProperty("storage_key", out var k) && k.ValueKind == JsonValueKind.String, summary));
        }
        return list.OrderByDescending(v => v.Version).ToList();
    }

    /// <summary>The newest bridge file as JSON text, or null when the project has none yet. The caller validates it (units, version) exactly as for a file.</summary>
    public async Task<(CadBridgeVersion version, string json)?> DownloadLatestAsync(string aqcId, CancellationToken ct = default)
    {
        var latest = (await ListAsync(aqcId, ct)).FirstOrDefault(v => v.HasFile);
        if (latest is null) return null;
        var link = await _c.GetAsync<JsonElement>($"api/aqc/v1/files/version/{Uri.EscapeDataString(latest.Id)}", ct);
        var bytes = await _c.GetSignedAsync(link.GetProperty("url").GetString()!, ct);
        return (latest, Encoding.UTF8.GetString(bytes));
    }

    /// <summary>What AAD does: publish the measured take-off. The file's SHA-256 is the content hash, so re-publishing an unchanged take-off is a no-op.
    /// <paramref name="summary"/> should carry <c>drawing_id</c> (the file's own) so AQC can show where it came from.</summary>
    public Task<string> PublishAsync(string aqcId, string bridgeJson, JsonObject summary, CancellationToken ct = default)
        => _versions.PublishFileAsync(aqcId, VersionKinds.CadBridge, Encoding.UTF8.GetBytes(bridgeJson), "application/json", summary, ct);
}
