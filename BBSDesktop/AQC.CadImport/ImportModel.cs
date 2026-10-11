// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

namespace Aqc.CadImport;

public sealed class LevelRec
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public double HeightMm { get; set; }
    public double SlabThicknessMm { get; set; }
    public double BeamDepthMm { get; set; }
}

/// <summary>What the importer reads and writes. BBSApp adapts ProjectStore to this; tests use it in memory.</summary>
public sealed class ImportModel
{
    public IList<LevelRec> Levels { get; init; } = new List<LevelRec>();
    /// <summary>Keys: name, client_name, company_name, location, address.</summary>
    public IDictionary<string, string> Project { get; init; } = new Dictionary<string, string>();
    /// <summary>Take-off sheets by category: masonry, masonry_openings, doors, windows, columns, lintels, flooring, slabs.</summary>
    public IDictionary<string, IList<Dictionary<string, string>>> Sheets { get; init; } =
        new Dictionary<string, IList<Dictionary<string, string>>>(StringComparer.OrdinalIgnoreCase);

    public IList<Dictionary<string, string>> Sheet(string category)
    {
        if (!Sheets.TryGetValue(category, out var s))
            Sheets[category] = s = new List<Dictionary<string, string>>();
        return s;
    }
}

public enum PlanAction { Add, Update, Unchanged, Skipped, Flagged, NoLongerInDrawing }

public sealed class PlanItem
{
    public string Section { get; init; } = "";
    public PlanAction Action { get; init; }
    public string Ref { get; init; } = "";
    public string Mark { get; init; } = "";
    public string Reason { get; init; } = "";
    /// <summary>Row to add (Add) or the new field values (Update).</summary>
    public Dictionary<string, string>? Row { get; init; }
    /// <summary>The existing row an Update changes.</summary>
    public Dictionary<string, string>? Target { get; init; }
    public LevelRec? LevelTarget { get; init; }
    public override string ToString() => $"{Action} {Section} {Mark} {Ref} {Reason}".Trim();
}

public sealed class ImportOptions
{
    /// <summary>When false (default) a project field already filled in AQC is left alone and reported.</summary>
    public bool OverwriteProject { get; init; }
    /// <summary>When true, rows edited in AQC after import are overwritten too.</summary>
    public bool OverwriteEdited { get; init; }
}

public sealed class ImportPlan
{
    public string DrawingId { get; init; } = "";
    public List<PlanItem> Items { get; } = new();
    /// <summary>Set when AQC already holds rows from another drawing; the caller should ask before importing.</summary>
    public string? OtherDrawingId { get; set; }
    public IEnumerable<PlanItem> Of(PlanAction a) => Items.Where(i => i.Action == a);
    public int Count(PlanAction a) => Items.Count(i => i.Action == a);
    public bool HasChanges => Items.Any(i => i.Action is PlanAction.Add or PlanAction.Update);
    public string Summary =>
        $"{Count(PlanAction.Add)} to add, {Count(PlanAction.Update)} to update, {Count(PlanAction.Unchanged)} unchanged, " +
        $"{Count(PlanAction.Flagged)} flagged, {Count(PlanAction.Skipped)} skipped, {Count(PlanAction.NoLongerInDrawing)} no longer in the drawing";
}
