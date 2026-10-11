// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aorms.Connect;

/// <summary>One synced row: a section name, a stable row id and its fields.</summary>
public sealed record SyncRow(string Section, string RowId, JsonObject Fields, bool Deleted = false);

/// <summary>A project split for syncing: the project-level settings skeleton plus every row.</summary>
public sealed record SyncProject(JsonObject Settings, List<SyncRow> Rows);

/// <summary>
/// Maps a <c>.bbsproj</c> (v17) document to the contract's rows + settings (AQC-CLIENT-SPEC §5) and back.
/// Take-off rows carry no id, so one is minted and stored inside the row as <c>_rid</c>; it round-trips in the file.
/// <c>last_estimate</c> and <c>takeoff</c> never sync (a version, and a local file path). Pure and platform-neutral.
/// </summary>
public static class ProjectMapper
{
    public const string RowIdKey = "_rid";
    public const int FormatVersion = 17;

    static readonly string[] TakeoffSections =
    {
        "columns", "beams", "pedestals", "lintels", "slabs", "footings", "walls", "stairs",
        "masonry", "masonry_openings", "plaster", "finish_propose", "pcc", "earthwork", "ssm", "shuttering", "flooring",
        "painting", "waterproofing", "dpc", "coping", "screed", "vdf", "skirting", "parapet", "plinth_protection", "doors", "windows",
    };

    // (section, path to the array, id key) — plain id-keyed arrays.
    static readonly (string Section, string[] Path)[] IdSections =
    {
        ("schedule_activities", new[] { "schedule", "activities" }),
        ("transactions", new[] { "accounts", "transactions" }),
        ("documents", new[] { "office", "documents" }),
        ("suppliers", new[] { "stores", "suppliers" }), ("warehouses", new[] { "stores", "warehouses" }),
        ("orders", new[] { "stores", "orders" }), ("grns", new[] { "stores", "grns" }), ("issues", new[] { "stores", "issues" }),
        ("sites", new[] { "org", "sites" }), ("resources", new[] { "org", "resources" }),
        ("employees", new[] { "org", "employees" }), ("payroll", new[] { "org", "payroll" }),
        ("link_rules", new[] { "link_rules" }),
    };

    // (parent section, line section, path, child key)
    static readonly (string Parent, string Line, string[] Path)[] ParentLineSections =
    {
        ("contracts", "contract_lines", new[] { "contracts", "contracts" }),
        ("bills", "bill_lines", new[] { "accounts", "bills" }),
    };

    public static string NewRowId() => Guid.NewGuid().ToString("N");

    /// <summary>Splits a project document into settings + rows. Mints <c>_rid</c> on take-off rows that lack one (mutates <paramref name="doc"/>).</summary>
    public static SyncProject ToSync(JsonObject doc)
    {
        var rows = new List<SyncRow>();
        var settings = (JsonObject)doc.DeepClone();
        settings.Remove("last_estimate");
        settings.Remove("takeoff");

        foreach (var s in TakeoffSections)
        {
            if (doc[s] is not JsonArray arr) continue;
            foreach (var item in arr)
            {
                if (item is not JsonObject o) continue;
                if (o[RowIdKey] is null || string.IsNullOrEmpty(o[RowIdKey]!.GetValue<string>())) o[RowIdKey] = NewRowId();
                rows.Add(new SyncRow(s, o[RowIdKey]!.GetValue<string>(), (JsonObject)o.DeepClone()));
            }
            settings[s] = new JsonArray();
        }
        foreach (var (section, path) in IdSections)
        {
            var arr = Find(doc, path);
            if (arr is null) continue;
            foreach (var item in arr)
                if (item is JsonObject o && IdOf(o) is { } id) rows.Add(new SyncRow(section, id, (JsonObject)o.DeepClone()));
            SetEmpty(settings, path);
        }
        foreach (var (parent, line, path) in ParentLineSections)
        {
            var arr = Find(doc, path);
            if (arr is null) continue;
            foreach (var item in arr)
            {
                if (item is not JsonObject o || IdOf(o) is not { } id) continue;
                var head = (JsonObject)o.DeepClone();
                head.Remove("lines");
                rows.Add(new SyncRow(parent, id, head));
                if (o["lines"] is JsonArray lines)
                    for (var i = 0; i < lines.Count; i++)
                        if (lines[i] is JsonObject lo) rows.Add(new SyncRow(line, $"{id}:{i}", (JsonObject)lo.DeepClone()));
            }
            SetEmpty(settings, path);
        }
        return new SyncProject(settings, rows);
    }

    /// <summary>Rebuilds a project document from settings + rows (deleted rows are skipped).</summary>
    public static JsonObject FromSync(JsonObject settings, IEnumerable<SyncRow> rows)
    {
        var doc = (JsonObject)settings.DeepClone();
        var live = rows.Where(r => !r.Deleted).ToList();

        foreach (var s in TakeoffSections)
        {
            var mine = live.Where(r => r.Section == s).ToList();
            if (mine.Count == 0 && doc[s] is null) continue;
            var arr = new JsonArray();
            foreach (var r in mine) { var f = (JsonObject)r.Fields.DeepClone(); f[RowIdKey] = r.RowId; arr.Add(f); }
            doc[s] = arr;
        }
        foreach (var (section, path) in IdSections)
        {
            var mine = live.Where(r => r.Section == section).ToList();
            if (mine.Count == 0 && Find(doc, path) is null) continue;
            var arr = new JsonArray();
            foreach (var r in mine) arr.Add(r.Fields.DeepClone());
            SetArray(doc, path, arr);
        }
        foreach (var (parent, line, path) in ParentLineSections)
        {
            var heads = live.Where(r => r.Section == parent).ToList();
            if (heads.Count == 0 && Find(doc, path) is null) continue;
            var arr = new JsonArray();
            foreach (var h in heads)
            {
                var o = (JsonObject)h.Fields.DeepClone();
                var ls = new JsonArray();
                foreach (var l in live.Where(r => r.Section == line && r.RowId.StartsWith(h.RowId + ":", StringComparison.Ordinal))
                             .OrderBy(r => int.TryParse(r.RowId[(h.RowId.Length + 1)..], out var n) ? n : int.MaxValue))
                    ls.Add(l.Fields.DeepClone());
                o["lines"] = ls;
                arr.Add(o);
            }
            SetArray(doc, path, arr);
        }
        return doc;
    }

    /// <summary>Contract wire shape for <c>POST /projects/{id}/rows</c> and <c>POST /projects</c>.</summary>
    public static JsonArray ToWire(IEnumerable<SyncRow> rows)
    {
        var arr = new JsonArray();
        foreach (var r in rows)
        {
            var o = new JsonObject { ["section"] = r.Section, ["row_id"] = r.RowId, ["fields"] = r.Fields.DeepClone() };
            if (r.Deleted) o["deleted"] = true;
            arr.Add(o);
        }
        return arr;
    }

    public static SyncRow FromWire(JsonElement e) => new(
        e.GetProperty("section").GetString()!, e.GetProperty("row_id").GetString()!,
        e.TryGetProperty("fields", out var f) && f.ValueKind == JsonValueKind.Object ? (JsonObject)JsonNode.Parse(f.GetRawText())! : new JsonObject(),
        e.TryGetProperty("deleted", out var d) && d.ValueKind == JsonValueKind.True);

    static string? IdOf(JsonObject o) => o["id"] switch { null => null, var n => n.ToString() is { Length: > 0 } s ? s : null };

    static JsonArray? Find(JsonNode root, string[] path)
    {
        JsonNode? n = root;
        foreach (var p in path) n = (n as JsonObject)?[p];
        return n as JsonArray;
    }
    static void SetEmpty(JsonObject root, string[] path) { if (Find(root, path) is not null) SetArray(root, path, new JsonArray()); }
    static void SetArray(JsonObject root, string[] path, JsonArray arr)
    {
        var o = root;
        for (var i = 0; i < path.Length - 1; i++)
        {
            if (o[path[i]] is not JsonObject next) { next = new JsonObject(); o[path[i]] = next; }
            o = next;
        }
        o[path[^1]] = arr;
    }
}
