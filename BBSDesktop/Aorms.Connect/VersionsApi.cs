// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aorms.Connect;

/// <summary>Version kinds and the capability each needs (AQC-CLIENT-SPEC §6). The server enforces this too; the client uses it to hide actions.</summary>
public static class VersionKinds
{
    public const string Estimate = "estimate", Boq = "boq", Schedule = "schedule", RunningBill = "running_bill", Ipc = "ipc",
        FinalAccount = "final_account", Bbs = "bbs", JointMeasurement = "joint_measurement", CadBridge = "cad_bridge";

    public static string RequiredCapability(string kind) => kind switch
    {
        Estimate or Boq or Schedule => "fees:manage",
        RunningBill or Ipc or FinalAccount => "cost:approve",
        Bbs or JointMeasurement or CadBridge => "write",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown version kind."),
    };

    public static bool CanAdd(SessionInfo s, string kind) => s.Can(RequiredCapability(kind));
}

/// <summary>AQC works in rupee doubles; AORMS in integer paise.</summary>
public static class Paise
{
    /// <summary>Rupees → paise, rounding half away from zero (so 0.005 → 1, -0.005 → -1).</summary>
    public static long FromRupees(double rupees) => (long)Math.Round(rupees * 100d, MidpointRounding.AwayFromZero);
    public static long FromRupees(decimal rupees) => (long)Math.Round(rupees * 100m, MidpointRounding.AwayFromZero);
}

public sealed record UploadedFile(string StorageKey, string Sha256, long SizeBytes);

/// <summary>Publishing versions (estimate, BOQ, BBS, schedule, IPC …) with an optional attached file.</summary>
public sealed class VersionsApi
{
    public static readonly HashSet<string> AllowedContentTypes = new()
    {
        "application/pdf", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "text/csv", "image/png", "image/jpeg", "application/json",
    };
    public const long MaxFileBytes = 25L * 1024 * 1024;

    readonly ConnectClient _c;
    public VersionsApi(ConnectClient client) => _c = client;

    public static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    /// <summary>Content hash for a non-file version: hash of the canonical JSON the version is built from.</summary>
    public static string HashJson(JsonNode node) => Sha256Hex(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()));

    /// <summary>Adds a version. The same <paramref name="contentHash"/> for the same kind returns the existing version's id (idempotent). Returns the version number as text.</summary>
    public async Task<string> AddAsync(string aqcId, string kind, string contentHash, JsonObject summary, string? storageKey = null, CancellationToken ct = default)
    {
        Guard(kind);
        var j = await _c.PostAsync<JsonElement>($"api/aqc/v1/projects/{aqcId}/versions", new { kind, contentHash, summary, storageKey }, ct);
        // The server answers { version: <n> } — the version number (new or existing); tolerate a string id or an object with an id too.
        var v = j.GetProperty("version");
        return v.ValueKind switch
        {
            JsonValueKind.Object => v.GetProperty("id").ToString(),
            JsonValueKind.String => v.GetString()!,
            _ => v.GetRawText(),
        };
    }

    /// <summary>Steps 1–2 of attaching a file: ask for a signed URL, PUT the bytes. Returns the key to pass to <see cref="AddAsync"/>.</summary>
    public async Task<UploadedFile> UploadAsync(string aqcId, string kind, byte[] bytes, string contentType, CancellationToken ct = default)
    {
        Guard(kind);
        if (!AllowedContentTypes.Contains(contentType)) throw new ArgumentException("Upload a PDF, XLSX, CSV, PNG or JPEG.", nameof(contentType));
        if (bytes.Length == 0) throw new ArgumentException("The file is empty.", nameof(bytes));
        if (bytes.Length > MaxFileBytes) throw new ArgumentException("Files can be up to 25 MB.", nameof(bytes));
        var sha = Sha256Hex(bytes);
        var init = await _c.PostAsync<JsonElement>($"api/aqc/v1/projects/{aqcId}/files", new { kind, contentType, sizeBytes = bytes.Length, sha256 = sha }, ct);
        var key = init.GetProperty("storageKey").GetString()!;
        await _c.PutSignedAsync(init.GetProperty("uploadUrl").GetString()!, bytes, contentType, ct);
        return new UploadedFile(key, sha, bytes.Length);
    }

    /// <summary>Uploads a file and publishes it as a version in one go. The file's SHA-256 is the content hash, so republishing the same file is a no-op.</summary>
    public async Task<string> PublishFileAsync(string aqcId, string kind, byte[] bytes, string contentType, JsonObject summary, CancellationToken ct = default)
    {
        var up = await UploadAsync(aqcId, kind, bytes, contentType, ct);
        return await AddAsync(aqcId, kind, up.Sha256, summary, up.StorageKey, ct);
    }

    /// <summary>Summary for an estimate version: AORMS shows <c>grandTotalPaise</c> to staff and (once released) the client.</summary>
    public static JsonObject EstimateSummary(double grandTotalRupees, string? label = null)
    {
        var o = new JsonObject { ["grandTotalPaise"] = Paise.FromRupees(grandTotalRupees) };
        if (!string.IsNullOrWhiteSpace(label)) o["label"] = label;
        return o;
    }

    /// <summary>Summary for an IPC: <paramref name="aormsBillId"/> is what makes it visible to that contractor.</summary>
    public static JsonObject IpcSummary(string aormsBillId, double grossRupees, double netRupees) => new()
    {
        ["billId"] = aormsBillId, ["grossPaise"] = Paise.FromRupees(grossRupees), ["netPaise"] = Paise.FromRupees(netRupees),
    };

    public static JsonObject FinalAccountSummary(double finalValueRupees) => new() { ["finalValuePaise"] = Paise.FromRupees(finalValueRupees) };

    void Guard(string kind) { _ = VersionKinds.RequiredCapability(kind); }
}
