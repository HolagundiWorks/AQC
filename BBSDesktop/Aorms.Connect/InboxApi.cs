// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Text.Json;

namespace Aorms.Connect;

public sealed record BillLine(string Description, string? Unit, double PreviousQty, double ThisQty, long RatePaise, long AmountPaise);

/// <summary>A contractor's running-bill claim waiting for AQC to check and certify. All money is integer paise; percentages are as the contractor chose them.</summary>
public sealed record InboxBill(
    string Id, string ProjectOfficeId, string? PackageId, string? Ref, int? BillNo, string? PeriodStart, string? PeriodEnd, string Status,
    long GrossPaise, double RetentionPct, double GstPct, double TdsPct, double CessPct, double GstTdsPct, long AdvanceRecoveryPaise,
    string? Narrative, string? BackupName, bool HasBackup, string? SubmittedAt, IReadOnlyList<BillLine> Lines);

/// <summary>A progress update, joint-measurement, site-visit or meeting request from a contractor.</summary>
public sealed record InboxSubmission(string Id, string ProjectOfficeId, string Kind, string? Subject, string? Body, string Status, double? PercentComplete, string? MeetingAt, string? CreatedAt);

public sealed record Inbox(IReadOnlyList<InboxBill> Bills, IReadOnlyList<InboxSubmission> Submissions, string Now);

/// <summary>Deductions AQC computed for a bill, in paise. AQC never changes the gross; gross corrections go back to the contractor.</summary>
public sealed record Certification(
    long RetentionPaise, long GstPaise = 0, long TdsPaise = 0, long CessPaise = 0, long GstTdsPaise = 0, long AdvanceRecoveryPaise = 0, long OtherDeductionPaise = 0)
{
    /// <summary>Net payable, same formula the server uses for the contractor's statement.</summary>
    public long NetPaise(long grossPaise) => grossPaise + GstPaise - RetentionPaise - TdsPaise - CessPaise - GstTdsPaise - AdvanceRecoveryPaise - OtherDeductionPaise;
}

public sealed record CertifyResult(bool Certified, long? Version);

/// <summary>Contractor bills loop (AQC-CLIENT-SPEC §7): read the inbox, fetch a bill's backup, certify.</summary>
public sealed class InboxApi
{
    readonly ConnectClient _c;
    public InboxApi(ConnectClient client) => _c = client;

    /// <summary>Everything waiting. Pass the previous result's <c>Now</c> as <paramref name="since"/> to get only what is new.</summary>
    public async Task<Inbox> GetAsync(string? projectOfficeId = null, string? since = null, CancellationToken ct = default)
    {
        var q = new List<string>();
        if (projectOfficeId is not null) q.Add("projectOfficeId=" + Uri.EscapeDataString(projectOfficeId));
        if (since is not null) q.Add("since=" + Uri.EscapeDataString(since));
        var j = await _c.GetAsync<JsonElement>("api/aqc/v1/inbox" + (q.Count > 0 ? "?" + string.Join("&", q) : ""), ct);
        var bills = new List<InboxBill>();
        foreach (var b in j.GetProperty("bills").EnumerateArray())
        {
            var lines = new List<BillLine>();
            if (b.TryGetProperty("lines", out var ls) && ls.ValueKind == JsonValueKind.Array)
                foreach (var l in ls.EnumerateArray())
                    lines.Add(new BillLine(S(l, "description") ?? "", S(l, "unit"), D(l, "previous_qty"), D(l, "this_qty"), L(l, "rate_paise"), L(l, "amount_paise")));
            bills.Add(new InboxBill(S(b, "id")!, S(b, "project_id") ?? "", S(b, "package_id"), S(b, "ref"), b.TryGetProperty("bill_no", out var bn) && bn.ValueKind == JsonValueKind.Number ? bn.GetInt32() : null,
                S(b, "period_start"), S(b, "period_end"), S(b, "status") ?? "", L(b, "gross_paise"), D(b, "retention_pct"), D(b, "gst_pct"), D(b, "tds_pct"), D(b, "cess_pct"), D(b, "gst_tds_pct"),
                L(b, "advance_recovery_paise"), S(b, "narrative"), S(b, "attachment_name"), b.TryGetProperty("backup", out var bk) && bk.ValueKind == JsonValueKind.String, S(b, "submitted_at"), lines));
        }
        var subs = new List<InboxSubmission>();
        foreach (var s in j.GetProperty("submissions").EnumerateArray())
            subs.Add(new InboxSubmission(S(s, "id")!, S(s, "project_id") ?? "", S(s, "kind") ?? "", S(s, "subject"), S(s, "body"), S(s, "status") ?? "",
                s.TryGetProperty("percent_complete", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetDouble() : null, S(s, "meeting_at"), S(s, "created_at")));
        return new Inbox(bills, subs, S(j, "now") ?? DateTimeOffset.UtcNow.ToString("o"));
    }

    /// <summary>The contractor's attached measurement backup (PDF/XLSX …) for a bill. 503 storage_unavailable → try again later.</summary>
    public async Task<byte[]> DownloadBackupAsync(string billId, CancellationToken ct = default)
    {
        var j = await _c.GetAsync<JsonElement>($"api/aqc/v1/files/bill/{Uri.EscapeDataString(billId)}", ct);
        return await _c.GetSignedAsync(j.GetProperty("url").GetString()!, ct);
    }

    /// <summary>Records AQC's certification. With <paramref name="contentHash"/> + <paramref name="aqcProjectId"/> the server also stores the immutable IPC version.
    /// Throws <c>already_certified</c> (409) if it was done already, <c>forbidden</c> without <c>cost:approve</c>.</summary>
    public async Task<CertifyResult> CertifyAsync(string billId, Certification c, string? contentHash = null, string? aqcProjectId = null, CancellationToken ct = default)
    {
        if (c.RetentionPaise < 0 || c.GstPaise < 0 || c.TdsPaise < 0 || c.CessPaise < 0 || c.GstTdsPaise < 0 || c.AdvanceRecoveryPaise < 0 || c.OtherDeductionPaise < 0)
            throw new ArgumentException("Deductions can't be negative.", nameof(c));
        var j = await _c.PostAsync<JsonElement>($"api/aqc/v1/bills/{Uri.EscapeDataString(billId)}/certify", new
        {
            retentionPaise = c.RetentionPaise, gstPaise = c.GstPaise, tdsPaise = c.TdsPaise, cessPaise = c.CessPaise, gstTdsPaise = c.GstTdsPaise,
            advanceRecoveryPaise = c.AdvanceRecoveryPaise, otherDeductionPaise = c.OtherDeductionPaise, contentHash, aqcProjectId,
        }, ct);
        return new CertifyResult(j.TryGetProperty("certified", out var ok) && ok.ValueKind == JsonValueKind.True,
            j.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null);
    }

    static string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static double D(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
    static long L(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? (long)Math.Round(v.GetDouble()) : 0;
}
