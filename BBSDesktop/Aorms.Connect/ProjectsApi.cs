// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aorms.Connect;

public sealed record OnlineBinding(string Id, long HeadSeq, int FormatVersion, bool EditedByOther, string? UpdatedAt);
public sealed record OnlineProject(string ProjectOfficeId, string Ref, string Title, string? Status, string? City, OnlineBinding? Aqc)
{
    public bool IsBound => Aqc is not null;
}
/// <summary><c>Pending</c> = rows not yet sent (empty when the whole project went up).
public sealed record PushResult(string AqcProjectId, long HeadSeq, int Rows, IReadOnlyList<SyncRow> Pending);
public sealed record PulledProject(JsonObject Settings, List<SyncRow> Rows, long HeadSeq);

/// <summary>Online project list, push-online, pull and open-from-seed (AQC-CLIENT-SPEC §3).</summary>
public sealed class ProjectsApi
{
    readonly ConnectClient _c;
    public ProjectsApi(ConnectClient client) => _c = client;

    public async Task<List<OnlineProject>> ListAsync(CancellationToken ct = default)
    {
        var j = await _c.GetAsync<JsonElement>("api/aqc/v1/projects", ct);
        var list = new List<OnlineProject>();
        foreach (var p in j.GetProperty("projects").EnumerateArray())
        {
            OnlineBinding? b = null;
            if (p.TryGetProperty("aqc", out var a) && a.ValueKind == JsonValueKind.Object)
                b = new OnlineBinding(a.GetProperty("id").GetString()!, a.GetProperty("headSeq").GetInt64(),
                    a.TryGetProperty("formatVersion", out var fv) && fv.ValueKind == JsonValueKind.Number ? fv.GetInt32() : ProjectMapper.FormatVersion,
                    a.TryGetProperty("editedByOther", out var eo) && eo.ValueKind == JsonValueKind.True,
                    a.TryGetProperty("updatedAt", out var u) ? u.GetString() : null);
            list.Add(new OnlineProject(p.GetProperty("projectOfficeId").GetString()!, Str(p, "ref") ?? "", Str(p, "title") ?? "", Str(p, "status"), Str(p, "city"), b));
        }
        return list;
    }

    /// <summary>Title block + parties for a project that has no AQC binding yet. Returns the <c>project</c> and <c>parties</c> blocks.</summary>
    public async Task<JsonObject> GetSeedAsync(string projectOfficeId, CancellationToken ct = default)
    {
        var j = await _c.GetAsync<JsonElement>($"api/aqc/v1/seed/{Uri.EscapeDataString(projectOfficeId)}", ct);
        return (JsonObject)JsonNode.Parse(j.GetRawText())!;
    }

    /// <summary>Rows in the binding request; the rest follow in lease-guarded batches so a 10 000-row project never goes up as one body.</summary>
    public const int FirstBatch = ProjectSync.BatchSize;

    /// <summary>Binds a local project to an AORMS project: the first ≤500 rows go with the binding, the remainder in ≤500-row batches under the
    /// edit lease. The document is mutated so every take-off row carries its <c>_rid</c>. If a later batch fails the project IS bound with the
    /// rows so far — the caller keeps the unsent remainder (<see cref="PushResult.Pending"/>) in its outbox and carries on.</summary>
    public async Task<PushResult> PushOnlineAsync(string projectOfficeId, JsonObject projectDoc, CancellationToken ct = default)
    {
        var sync = ProjectMapper.ToSync(projectDoc);
        var first = sync.Rows.Take(FirstBatch).ToList();
        var j = await _c.PostAsync<JsonElement>("api/aqc/v1/projects", new
        {
            projectOfficeId,
            formatVersion = ProjectMapper.FormatVersion,
            settings = sync.Settings,
            rows = ProjectMapper.ToWire(first),
        }, ct);
        var id = j.GetProperty("aqcProjectId").GetString()!;
        long head = j.GetProperty("headSeq").GetInt64();
        var rest = sync.Rows.Skip(FirstBatch).ToList();
        var sent = first.Count;
        if (rest.Count > 0)
        {
            var box = new Outbox(new NullOutboxStore());
            box.UpsertMany(rest);
            var ps = new ProjectSync(_c, id, head, box);
            var flushed = await ps.FlushAsync(ct);
            sent += flushed.Sent; head = ps.HeadSeq;
            return new PushResult(id, head, sent, box.Snapshot());
        }
        return new PushResult(id, head, sent, Array.Empty<SyncRow>());
    }

    /// <summary>Pulls every row for an already-bound project, paging until done.</summary>
    public async Task<PulledProject> PullAsync(string aqcId, long since = 0, CancellationToken ct = default)
    {
        JsonObject settings = new();
        var rows = new List<SyncRow>();
        long head = since, cursor = since;
        while (true)
        {
            var j = await _c.GetAsync<JsonElement>($"api/aqc/v1/projects/{aqcId}/rows?since={cursor}", ct);
            if (j.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object) settings = (JsonObject)JsonNode.Parse(s.GetRawText())!;
            foreach (var r in j.GetProperty("rows").EnumerateArray()) rows.Add(ProjectMapper.FromWire(r));
            head = j.TryGetProperty("headSeq", out var h) ? h.GetInt64() : head;
            if (!(j.TryGetProperty("more", out var more) && more.ValueKind == JsonValueKind.True)) break;
            var next = j.GetProperty("nextSince").GetInt64();
            if (next <= cursor) break;
            cursor = next;
        }
        return new PulledProject(settings, rows, head);
    }

    static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
