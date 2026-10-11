// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Text.Json;

namespace Aorms.Connect;

/// <summary>One rate line. <c>Rate</c> is AQC's raw rupee value — never converted to paise.</summary>
public sealed record RateItem(string Code, string Category, string Description, string Unit, double Rate);

public sealed record OnlineRateBook(string Id, string ClientId, string Name, string Notes, int Revision, int ItemCount, bool IsActive, string? UpdatedAt);
public sealed record RateBookDetail(OnlineRateBook Version, IReadOnlyList<RateItem> Items);
public sealed record PublishRateBookResult(string VersionId, int Revision, bool Changed);

/// <summary>What AQC has locally for one rate book (<c>RateBookVersion</c>): its stable id and the revision it last synced.</summary>
public sealed record LocalRateBook(string ClientId, string Name, int Revision);

public enum RateBookAction { InSync, OfferUpdateFromOnline, OnlyOnline, OnlyLocal, LocalIsNewer }
public sealed record RateBookPlanEntry(RateBookAction Action, string ClientId, OnlineRateBook? Online, LocalRateBook? Local);

/// <summary>Shared rate books (AQC-CLIENT-SPEC §8): list, fetch, publish, activate, and the merge plan used on sign-in.</summary>
public sealed class RateBooksApi
{
    readonly ConnectClient _c;
    public RateBooksApi(ConnectClient client) => _c = client;

    public async Task<List<OnlineRateBook>> ListAsync(CancellationToken ct = default)
    {
        var j = await _c.GetAsync<JsonElement>("api/aqc/v1/rate-books", ct);
        return j.GetProperty("versions").EnumerateArray().Select(Parse).ToList();
    }

    public async Task<RateBookDetail> GetAsync(string id, CancellationToken ct = default)
    {
        var j = await _c.GetAsync<JsonElement>($"api/aqc/v1/rate-books/{Uri.EscapeDataString(id)}", ct);
        var items = j.GetProperty("items").EnumerateArray()
            .Select(i => new RateItem(S(i, "code") ?? "", S(i, "category") ?? "", S(i, "description") ?? "", S(i, "unit") ?? "", i.TryGetProperty("rate", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetDouble() : 0))
            .ToList();
        return new RateBookDetail(Parse(j.GetProperty("version")), items);
    }

    /// <summary>Publishes a book. The server hashes the content, so an unchanged re-publish answers <c>Changed=false</c> (200) without a new revision.
    /// Duplicate codes are refused locally and by the server (<c>duplicate_codes</c>). Needs <c>fees:manage</c>.</summary>
    public async Task<PublishRateBookResult> PublishAsync(string clientId, string name, string notes, IReadOnlyList<RateItem> items, bool activate, CancellationToken ct = default)
    {
        var dup = FindDuplicateCodes(items);
        if (dup.Count > 0) throw new ArgumentException("Duplicate item codes: " + string.Join(", ", dup.Take(5)), nameof(items));
        var j = await _c.PostAsync<JsonElement>("api/aqc/v1/rate-books", new
        {
            clientId, name, notes,
            items = items.Select(i => new { code = i.Code, category = i.Category, description = i.Description, unit = i.Unit, rate = i.Rate }),
            activate,
        }, ct);
        return new PublishRateBookResult(j.GetProperty("versionId").GetString()!, j.GetProperty("revision").GetInt32(), j.TryGetProperty("changed", out var c) && c.ValueKind == JsonValueKind.True);
    }

    /// <summary>Makes a book the studio's active one — every machine then shares it. Needs <c>fees:manage</c>.</summary>
    public Task<JsonElement> ActivateAsync(string id, CancellationToken ct = default) => _c.PostAsync<JsonElement>($"api/aqc/v1/rate-books/{Uri.EscapeDataString(id)}/activate", null, ct);

    public static List<string> FindDuplicateCodes(IEnumerable<RateItem> items)
    {
        var seen = new HashSet<string>(); var dup = new List<string>();
        foreach (var i in items) if (!seen.Add(i.Code) && !dup.Contains(i.Code)) dup.Add(i.Code);
        return dup;
    }

    /// <summary>Sign-in merge: match on <c>ClientId</c> (= AQC's <c>RateBookVersion.Id</c>). A higher online revision offers an update; nothing is overwritten silently.</summary>
    public static List<RateBookPlanEntry> Plan(IEnumerable<LocalRateBook> local, IEnumerable<OnlineRateBook> online)
    {
        var plan = new List<RateBookPlanEntry>();
        var locals = local.ToDictionary(l => l.ClientId, StringComparer.Ordinal);
        foreach (var o in online)
        {
            if (!locals.Remove(o.ClientId, out var l)) plan.Add(new(RateBookAction.OnlyOnline, o.ClientId, o, null));
            else if (o.Revision > l.Revision) plan.Add(new(RateBookAction.OfferUpdateFromOnline, o.ClientId, o, l));
            else if (o.Revision < l.Revision) plan.Add(new(RateBookAction.LocalIsNewer, o.ClientId, o, l));
            else plan.Add(new(RateBookAction.InSync, o.ClientId, o, l));
        }
        foreach (var l in locals.Values) plan.Add(new(RateBookAction.OnlyLocal, l.ClientId, null, l));
        return plan;
    }

    static OnlineRateBook Parse(JsonElement v) => new(S(v, "id")!, S(v, "client_id") ?? "", S(v, "name") ?? "", S(v, "notes") ?? "",
        v.TryGetProperty("revision", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 1,
        v.TryGetProperty("item_count", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0,
        v.TryGetProperty("is_active", out var a) && a.ValueKind == JsonValueKind.True, S(v, "updated_at"));

    static string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
