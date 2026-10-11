// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aqc.CadImport;

/// <summary>
/// "Export for CAD": writes the project details, the floors above the plinth and the beam depths
/// in the hcw-aqc-bridge v1 format, for AADT's reverse import. Geometry is never sent: the CAD
/// program applies these only after the user says Yes and never changes drawn geometry.
/// </summary>
public static class CadExporter
{
    static readonly string[] ProjectKeys = { "name", "client_name", "company_name", "prepared_by_name", "location", "address" };

    /// <summary>
    /// The drawing id this project last imported from (so the CAD program recognises the file),
    /// or null when nothing has been imported yet.
    /// </summary>
    public static string? KnownDrawingId(ImportModel model)
    {
        foreach (var sheet in model.Sheets.Values)
            foreach (var r in sheet)
                if (r.TryGetValue("source", out var s) && s.StartsWith("hcwcad:", StringComparison.Ordinal))
                    return s["hcwcad:".Length..];
        return null;
    }

    public static string Build(ImportModel model, DateTime? nowUtc = null)
    {
        var id = KnownDrawingId(model) ?? "aqc-" + Guid.NewGuid().ToString("D");
        var root = new JsonObject
        {
            ["format"] = "hcw-aqc-bridge",
            ["version"] = 1,
            ["source"] = new JsonObject
            {
                ["app"] = "AQC",
                ["drawing_id"] = id,
                ["exported"] = (nowUtc ?? DateTime.UtcNow).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            },
            ["units"] = "mm",
        };

        var project = new JsonObject();
        foreach (var k in ProjectKeys)
        {
            if (!model.Project.TryGetValue(k, out var v) || string.IsNullOrWhiteSpace(v)) continue;
            if (k == "name" && v == "Untitled Project") continue;
            project[k] = v.Trim();
        }
        if (project.Count > 0) root["project"] = project;

        // floors above the plinth only; Lvl0 stays with AQC
        var levels = new JsonArray();
        foreach (var l in model.Levels.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(l.Name) || l.HeightMm <= 0) continue;
            levels.Add(new JsonObject
            {
                ["name"] = l.Name,
                ["height_mm"] = l.HeightMm,
                ["slab_thickness_mm"] = l.SlabThicknessMm,
                ["beam_depth_mm"] = l.BeamDepthMm,
            });
        }
        if (levels.Count > 0) root["levels"] = levels;

        var depths = model.Levels.Select(l => l.BeamDepthMm).Where(d => d > 0).Distinct().OrderBy(d => d).ToList();
        if (depths.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var d in depths) arr.Add(d);
            root["beam_depths_mm"] = arr;
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() });
    }
}
