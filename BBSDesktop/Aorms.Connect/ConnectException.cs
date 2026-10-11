// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Text.Json;

namespace Aorms.Connect;

/// <summary>An error answer from AORMS: `{ "error": { "code", "message", ... } }` (docs/esti/AQC-CLIENT-SPEC.md §2).</summary>
public sealed class ConnectException : Exception
{
    public int Status { get; }
    public string Code { get; }
    /// <summary>Set for `not_connected`: PLAN_NOT_CONNECTED | EXPIRED | NO_STUDIO.</summary>
    public string? Reason { get; }
    public int? RetryAfterSeconds { get; }

    public ConnectException(int status, string code, string message, string? reason = null, int? retryAfter = null) : base(message)
    {
        Status = status; Code = code; Reason = reason; RetryAfterSeconds = retryAfter;
    }

    public bool IsSessionReplaced => Code == "session_replaced";
    public bool IsNotConnected => Code == "not_connected";
    public bool IsLeaseRequired => Code == "lease_required";
    public bool IsRateLimited => Code == "rate_limited";

    internal static ConnectException From(int status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e))
            {
                string code = e.TryGetProperty("code", out var c) ? c.GetString() ?? "error" : "error";
                string msg = e.TryGetProperty("message", out var m) ? m.GetString() ?? "Request failed." : "Request failed.";
                string? reason = e.TryGetProperty("reason", out var r) ? r.GetString() : null;
                int? retry = e.TryGetProperty("retryAfterSeconds", out var ra) && ra.TryGetInt32(out var n) ? n : null;
                return new ConnectException(status, code, msg, reason, retry);
            }
        }
        catch (JsonException) { /* fall through */ }
        return new ConnectException(status, status >= 500 ? "unavailable" : "error", status >= 500 ? "AORMS isn't available right now." : "Request failed.");
    }
}
