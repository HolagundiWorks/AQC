// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Globalization;
using System.Text.Json.Nodes;

namespace Aqc.CadImport;

/// <summary>
/// Turns a bridge file into an import plan and applies it. Idempotent: rows carry
/// source = "hcwcad:&lt;drawing_id&gt;" and source_ref = the CAD ref; a second import updates, never duplicates.
/// Never touches Lvl0, rows with another source, or derived (auto_*) rows.
/// </summary>
public static class CadImporter
{
    const string Masonry = "masonry", MOpen = "masonry_openings", Doors = "doors", Windows = "windows",
        Columns = "columns", Lintels = "lintels", Flooring = "flooring", Slabs = "slabs";

    static readonly Dictionary<string, string[]> GeometryKeys = new()
    {
        [Masonry] = new[] { "length", "height", "thickness" },
        [MOpen] = new[] { "opening_l", "opening_h", "opening_kind", "wall_mark" },
        [Doors] = new[] { "nos", "width", "height", "door_type" },
        [Windows] = new[] { "nos", "width", "height" },
        [Columns] = new[] { "width", "depth" },
        [Lintels] = new[] { "opening", "bearing", "width", "depth" },
        [Flooring] = new[] { "length", "breadth" },
        [Slabs] = new[] { "span_x", "span_y", "thickness" },
    };

    static readonly Dictionary<string, string> Prefix = new()
    {
        [Masonry] = "MW", [MOpen] = "OP", [Columns] = "C", [Lintels] = "L", [Flooring] = "FL", [Slabs] = "S",
    };

    public static string SourceTag(string drawingId) => "hcwcad:" + drawingId;

    public static ImportPlan Plan(BridgeFile file, ImportModel model, ImportOptions? options = null)
    {
        options ??= new ImportOptions();
        var plan = new ImportPlan { DrawingId = file.DrawingId };
        var tag = SourceTag(file.DrawingId);

        // Another drawing's rows already present?
        foreach (var sheet in model.Sheets.Values)
            foreach (var r in sheet)
                if (r.TryGetValue("source", out var s) && s.StartsWith("hcwcad:", StringComparison.Ordinal) && s != tag)
                    plan.OtherDrawingId ??= s["hcwcad:".Length..];

        var levelMap = PlanLevels(file, model, plan);
        PlanProject(file, model, plan, options);

        // marks assigned during planning, so NextMark sees rows already planned
        var pending = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var wallMarkByRef = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        string? Lvl(JsonObject o, string section, string refId, string mark)
        {
            var name = BridgeFile.Str(o, "level");
            if (levelMap.TryGetValue(name, out var id)) return id;
            plan.Items.Add(new PlanItem { Section = section, Action = PlanAction.Skipped, Ref = refId, Mark = mark, Reason = $"level '{name}' is not in the file's levels" });
            return null;
        }

        void Upsert(string section, string refId, Dictionary<string, string> fields, string level, string? flag = null)
        {
            if (!seen.TryGetValue(section, out var set)) seen[section] = set = new(StringComparer.OrdinalIgnoreCase);
            set.Add(refId);
            var sheet = model.Sheet(section);
            var existing = sheet.FirstOrDefault(r => Get(r, "source") == tag && Get(r, "source_ref") == refId);
            var geo = GeometryKeys[section];
            if (existing is null)
            {
                var row = new Dictionary<string, string>(fields, StringComparer.OrdinalIgnoreCase);
                if (!row.ContainsKey("mark") || string.IsNullOrEmpty(row["mark"]))
                    row["mark"] = NextMark(section, level, sheet, pending);
                row["level"] = level;
                row["source"] = tag;
                row["source_ref"] = refId;
                row["source_hash"] = Hash(row, geo);
                if (!pending.TryGetValue(section, out var pl)) pending[section] = pl = new();
                pl.Add(row["mark"]);
                plan.Items.Add(new PlanItem { Section = section, Action = PlanAction.Add, Ref = refId, Mark = row["mark"], Reason = flag ?? "", Row = row });
                return;
            }
            var mark = Get(existing, "mark");
            var probe = new Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase);
            foreach (var k in geo) if (fields.TryGetValue(k, out var v)) probe[k] = v;
            var edited = Get(existing, "source_hash") != Hash(existing, geo);
            if (Hash(probe, geo) == Hash(existing, geo) && !edited)
            {
                plan.Items.Add(new PlanItem { Section = section, Action = PlanAction.Unchanged, Ref = refId, Mark = mark });
                return;
            }
            if (edited && !options.OverwriteEdited)
            {
                plan.Items.Add(new PlanItem { Section = section, Action = PlanAction.Flagged, Ref = refId, Mark = mark, Reason = "edited in AQC since the last import; left alone" });
                return;
            }
            var change = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in geo) if (fields.TryGetValue(k, out var v)) change[k] = v;
            change["source_hash"] = Hash(probe, geo);
            plan.Items.Add(new PlanItem { Section = section, Action = PlanAction.Update, Ref = refId, Mark = mark, Reason = flag ?? "", Row = change, Target = existing });
        }

        // walls
        foreach (var w in file.Walls)
        {
            var r = BridgeFile.Str(w, "ref");
            var lvl = Lvl(w, Masonry, r, r); if (lvl is null) continue;
            var thick = BridgeFile.Num(w, "thickness_mm") ?? 230;
            var f = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["length"] = N(BridgeFile.Num(w, "length_mm")), ["height"] = N(BridgeFile.Num(w, "height_mm")),
                ["thickness"] = N(thick),
                ["unit_type"] = BridgeFile.Str(w, "unit_type") is { Length: > 0 } ut ? ut : "Brick",
            };
            Upsert(Masonry, r, f, lvl, thick <= 120 ? "thickness 120 mm or less: AQC measures it as its 110 mm wall" : null);
            wallMarkByRef[r] = plan.Items.Last(i => i.Section == Masonry && i.Ref == r).Mark;
        }

        // openings: one masonry_openings row each (they deduct from the wall); door/window schedule rows are for counts only
        foreach (var o in file.Openings)
        {
            var r = BridgeFile.Str(o, "ref");
            var mark = BridgeFile.Str(o, "mark");
            var lvl = Lvl(o, MOpen, r, mark); if (lvl is null) continue;
            var kind = BridgeFile.Str(o, "kind");
            var wallRef = BridgeFile.Str(o, "wall_ref");
            string wallMark = "", flag = "";
            if (wallRef.Length > 0 && wallMarkByRef.TryGetValue(wallRef, out var wm)) wallMark = wm;
            else flag = "no wall in the file for this opening; added without a wall, so it will not deduct";
            Upsert(MOpen, r, new(StringComparer.OrdinalIgnoreCase)
            {
                ["wall_mark"] = wallMark, ["nos"] = "1",
                ["opening_l"] = N(BridgeFile.Num(o, "width_mm")), ["opening_h"] = N(BridgeFile.Num(o, "height_mm")),
                ["opening_kind"] = kind.Equals("Window", StringComparison.OrdinalIgnoreCase) ? "Window" : "Door",
                ["takeoff_id"] = tag + "/" + r,
            }, lvl, flag.Length > 0 ? flag : null);
        }

        // door / window schedule rows: one per mark and level
        var groups = file.Openings
            .Select(o => (o, lvl: levelMap.GetValueOrDefault(BridgeFile.Str(o, "level"))))
            .Where(x => x.lvl is not null)
            .GroupBy(x => (kind: BridgeFile.Str(x.o, "kind"), mark: BridgeFile.Str(x.o, "mark"), x.lvl));
        foreach (var g in groups)
        {
            var first = g.First().o;
            var isWin = g.Key.kind.Equals("Window", StringComparison.OrdinalIgnoreCase);
            var sec = isWin ? Windows : Doors;
            var f = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["mark"] = g.Key.mark, ["nos"] = g.Count().ToString(CultureInfo.InvariantCulture),
                ["width"] = N(BridgeFile.Num(first, "width_mm")), ["height"] = N(BridgeFile.Num(first, "height_mm")),
                ["wall_mark"] = "", ["deduct_from_wall"] = "No",   // the masonry_openings rows already deduct
            };
            if (!isWin) f["door_type"] = BridgeFile.Str(first, "type");
            Upsert(sec, $"sched:{g.Key.mark}:{g.Key.lvl}", f, g.Key.lvl!);
        }

        // schedule counts must match the imported openings
        foreach (var s in file.OpeningSchedule)
        {
            var m = BridgeFile.Str(s, "mark");
            var want = (int)(BridgeFile.Num(s, "nos") ?? 0);
            var got = file.Openings.Count(o => BridgeFile.Str(o, "mark") == m);
            if (want != got)
                plan.Items.Add(new PlanItem { Section = "opening_schedule", Action = PlanAction.Flagged, Mark = m, Reason = $"schedule says {want}, drawing has {got} openings" });
        }

        foreach (var c in file.Columns)
        {
            var r = BridgeFile.Str(c, "ref"); var m = BridgeFile.Str(c, "mark");
            var lvl = Lvl(c, Columns, r, m); if (lvl is null) continue;
            // no height: AQC computes it from the level (height - slab - beam depth)
            Upsert(Columns, r, new(StringComparer.OrdinalIgnoreCase)
            {
                ["nos"] = "1", ["width"] = N(BridgeFile.Num(c, "width_mm")), ["depth"] = N(BridgeFile.Num(c, "depth_mm")),
                ["notes"] = m.Length > 0 ? "CAD mark " + m : "",
            }, lvl);
        }

        foreach (var l in file.Lintels)
        {
            var r = BridgeFile.Str(l, "ref"); var m = BridgeFile.Str(l, "mark");
            var lvl = Lvl(l, Lintels, r, m); if (lvl is null) continue;
            Upsert(Lintels, r, new(StringComparer.OrdinalIgnoreCase)
            {
                ["nos"] = "1", ["opening"] = N(BridgeFile.Num(l, "opening_mm")), ["bearing"] = N(BridgeFile.Num(l, "bearing_mm") ?? 150),
                ["width"] = N(BridgeFile.Num(l, "width_mm")), ["depth"] = N(BridgeFile.Num(l, "depth_mm")),
                ["notes"] = m.Length > 0 ? "CAD mark " + m : "",
            }, lvl);
        }

        foreach (var rm in file.Rooms)
        {
            var r = BridgeFile.Str(rm, "ref"); var name = BridgeFile.Str(rm, "name");
            var lvl = Lvl(rm, Flooring, r, name); if (lvl is null) continue;
            var len = BridgeFile.Num(rm, "length_mm"); var br = BridgeFile.Num(rm, "breadth_mm");
            if (len is null || br is null || len <= 0 || br <= 0)
            {
                plan.Items.Add(new PlanItem { Section = Flooring, Action = PlanAction.Skipped, Ref = r, Mark = name, Reason = "room has no length and breadth (area only); add it by hand" });
                continue;
            }
            Upsert(Flooring, r, new(StringComparer.OrdinalIgnoreCase)
            {
                ["surface_kind"] = "Floor", ["length"] = N(len), ["breadth"] = N(br), ["notes"] = name,
            }, lvl);
        }

        foreach (var s in file.Slabs)
        {
            var r = BridgeFile.Str(s, "ref");
            var lvl = Lvl(s, Slabs, r, r); if (lvl is null) continue;
            var rect = s["rectangular"] is JsonValue jv && jv.TryGetValue<bool>(out var b) && b;
            var len = BridgeFile.Num(s, "length_mm"); var br = BridgeFile.Num(s, "breadth_mm");
            if (!rect || len is null || br is null || len <= 0 || br <= 0)
            {
                plan.Items.Add(new PlanItem { Section = Slabs, Action = PlanAction.Skipped, Ref = r, Reason = "non-rectangular slab; add it by hand" });
                continue;
            }
            Upsert(Slabs, r, new(StringComparer.OrdinalIgnoreCase)
            {
                ["span_x"] = N(len), ["span_y"] = N(br), ["thickness"] = N(BridgeFile.Num(s, "thickness_mm") ?? 150),
                ["slab_type"] = Math.Max(len.Value, br.Value) / Math.Min(len.Value, br.Value) <= 2 ? "Two-Way" : "One-Way",
            }, lvl);
        }

        // rows we imported earlier that the drawing no longer has: report, never delete
        foreach (var (section, sheet) in model.Sheets)
            foreach (var row in sheet)
                if (Get(row, "source") == tag && Get(row, "source_ref") is { Length: > 0 } sr
                    && !(seen.TryGetValue(section, out var set) && set.Contains(sr)))
                    plan.Items.Add(new PlanItem { Section = section, Action = PlanAction.NoLongerInDrawing, Ref = sr, Mark = Get(row, "mark"), Reason = "in AQC but no longer in the drawing; not deleted" });

        return plan;
    }

    /// <summary>Applies the plan. The returned action reverses the whole import (one undo step).</summary>
    public static Action Apply(ImportPlan plan, ImportModel model)
    {
        var undo = new List<Action>();
        foreach (var it in plan.Items)
        {
            if (it.Action is not (PlanAction.Add or PlanAction.Update) || it.Row is null) continue;

            if (it.Section == "level")
            {
                if (it.LevelTarget is { } lt)
                {
                    var (h, s, b) = (lt.HeightMm, lt.SlabThicknessMm, lt.BeamDepthMm);
                    lt.HeightMm = D(it.Row, "height"); lt.SlabThicknessMm = D(it.Row, "slab"); lt.BeamDepthMm = D(it.Row, "beam");
                    undo.Add(() => { lt.HeightMm = h; lt.SlabThicknessMm = s; lt.BeamDepthMm = b; });
                }
                else
                {
                    var lr = it.Row;
                    var rec = new LevelRec { Id = lr["id"], Name = lr["name"], HeightMm = D(lr, "height"), SlabThicknessMm = D(lr, "slab"), BeamDepthMm = D(lr, "beam") };
                    model.Levels.Add(rec);
                    undo.Add(() => model.Levels.Remove(rec));
                }
            }
            else if (it.Section == "project")
            {
                var key = it.Row["key"];
                var had = model.Project.TryGetValue(key, out var oldV);
                model.Project[key] = it.Row["value"];
                undo.Add(() => { if (had) model.Project[key] = oldV!; else model.Project.Remove(key); });
            }
            else if (it.Action == PlanAction.Add)
            {
                var sheet = model.Sheet(it.Section);
                var row = it.Row;
                sheet.Add(row);
                undo.Add(() => sheet.Remove(row));
            }
            else if (it.Target is { } t)
            {
                var before = it.Row.Keys.ToDictionary(k => k, k => t.TryGetValue(k, out var v) ? v : null, StringComparer.OrdinalIgnoreCase);
                foreach (var kv in it.Row) t[kv.Key] = kv.Value;
                undo.Add(() => { foreach (var kv in before) { if (kv.Value is null) t.Remove(kv.Key); else t[kv.Key] = kv.Value; } });
            }
        }
        return () => { for (int i = undo.Count - 1; i >= 0; i--) undo[i](); };
    }

    // ---- levels and project ----

    static Dictionary<string, string> PlanLevels(BridgeFile file, ImportModel model, ImportPlan plan)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<LevelRec>();
        int nextIndex = model.Levels.Count;
        for (int i = 0; i < file.Levels.Count; i++)
        {
            var fl = file.Levels[i];
            // Lvl0 is AQC's plinth and is never matched
            var match = model.Levels.Skip(1).FirstOrDefault(l => !used.Contains(l) && l.Name.Equals(fl.Name, StringComparison.OrdinalIgnoreCase))
                        ?? (model.Levels.Count > i + 1 && !used.Contains(model.Levels[i + 1]) ? model.Levels[i + 1] : null);
            if (match is not null)
            {
                used.Add(match); map[fl.Name] = match.Id;
                var h = fl.HeightMm ?? match.HeightMm; var s = fl.SlabThicknessMm ?? match.SlabThicknessMm; var b = fl.BeamDepthMm ?? match.BeamDepthMm;
                if (Math.Abs(h - match.HeightMm) < 0.01 && Math.Abs(s - match.SlabThicknessMm) < 0.01 && Math.Abs(b - match.BeamDepthMm) < 0.01)
                    plan.Items.Add(new PlanItem { Section = "level", Action = PlanAction.Unchanged, Ref = fl.Name, Mark = match.Id });
                else
                    plan.Items.Add(new PlanItem { Section = "level", Action = PlanAction.Update, Ref = fl.Name, Mark = match.Id,
                        Reason = "height is floor to floor in CAD; AQC uses slab-top to slab-top, so check the finish allowance",
                        Row = new() { ["height"] = N(h), ["slab"] = N(s), ["beam"] = N(b) }, LevelTarget = match });
            }
            else
            {
                var id = "Lvl" + nextIndex++;
                map[fl.Name] = id;
                plan.Items.Add(new PlanItem { Section = "level", Action = PlanAction.Add, Ref = fl.Name, Mark = id,
                    Row = new() { ["id"] = id, ["name"] = fl.Name, ["height"] = N(fl.HeightMm), ["slab"] = N(fl.SlabThicknessMm), ["beam"] = N(fl.BeamDepthMm) } });
            }
        }
        // a row may also name an existing AQC level id directly
        foreach (var l in model.Levels.Skip(1)) map.TryAdd(l.Id, l.Id);
        return map;
    }

    static void PlanProject(BridgeFile file, ImportModel model, ImportPlan plan, ImportOptions options)
    {
        foreach (var key in new[] { "name", "client_name", "company_name", "location", "address" })
        {
            if (!file.Project.TryGetValue(key, out var nv) || string.IsNullOrWhiteSpace(nv)) continue;
            model.Project.TryGetValue(key, out var cur);
            var empty = string.IsNullOrWhiteSpace(cur) || (key == "name" && cur == "Untitled Project");
            if (cur == nv) { plan.Items.Add(new PlanItem { Section = "project", Action = PlanAction.Unchanged, Ref = key }); continue; }
            if (empty || options.OverwriteProject)
                plan.Items.Add(new PlanItem { Section = "project", Action = PlanAction.Update, Ref = key, Mark = nv, Row = new() { ["key"] = key, ["value"] = nv } });
            else
                plan.Items.Add(new PlanItem { Section = "project", Action = PlanAction.Flagged, Ref = key, Mark = nv, Reason = $"AQC has '{cur}'; kept" });
        }
    }

    // ---- helpers ----

    static string NextMark(string section, string level, IList<Dictionary<string, string>> sheet, Dictionary<string, List<string>> pending)
    {
        var prefix = Prefix.GetValueOrDefault(section, "TK");
        var needle = prefix + "-" + level + "-";
        int max = 0;
        foreach (var m in sheet.Select(r => Get(r, "mark")).Concat(pending.GetValueOrDefault(section) ?? new()))
            if (m.StartsWith(needle, StringComparison.OrdinalIgnoreCase) && int.TryParse(m.AsSpan(needle.Length), out var n))
                max = Math.Max(max, n);
        return $"{needle}{(max + 1).ToString("000", CultureInfo.InvariantCulture)}";
    }

    static string Hash(Dictionary<string, string> row, string[] keys) => string.Join("|", keys.Select(k => Get(row, k)));
    static string Get(Dictionary<string, string> r, string k) => r.TryGetValue(k, out var v) ? v : "";
    static double D(Dictionary<string, string> r, string k) => double.TryParse(Get(r, k), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    static string N(double? v) => v is null ? "" : v.Value.ToString("0.##", CultureInfo.InvariantCulture);
}
