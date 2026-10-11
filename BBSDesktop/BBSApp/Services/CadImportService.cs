// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Human Centric Works, Hospet

using Aqc.CadImport;

namespace BBSApp.Services;

/// <summary>
/// Adapts <see cref="ProjectStore"/> to the CAD importer (AQC.CadImport). Take-off happens in
/// hcwCAD-KIT; this reads its .aqcbridge.json and merges it into the open project.
/// </summary>
public static class CadImportService
{
    static Action? _undo;
    public static bool CanUndo => _undo is not null;

    public sealed record Prepared(BridgeFile File, ImportModel Model, ImportPlan Plan);

    public static Prepared Prepare(string path, ImportOptions? options = null)
    {
        var file = BridgeFile.Parse(System.IO.File.ReadAllText(path));
        var model = BuildModel(ProjectStore.Current);
        return new Prepared(file, model, CadImporter.Plan(file, model, options));
    }

    /// <summary>Applies the plan to the open project. One undo step via <see cref="Undo"/>.</summary>
    public static void Apply(Prepared p)
    {
        var undo = CadImporter.Apply(p.Plan, p.Model);
        WriteBack(ProjectStore.Current, p.Model);
        _undo = () => { undo(); WriteBack(ProjectStore.Current, p.Model); ProjectStore.Current.Notify(); };
        ProjectStore.Current.Notify();
    }

    public static void Undo()
    {
        var u = _undo; _undo = null; u?.Invoke();
    }

    static ImportModel BuildModel(ProjectStore s)
    {
        var m = new ImportModel();
        foreach (var l in s.Levels)
            m.Levels.Add(new LevelRec { Id = l.Id, Name = l.Name, HeightMm = l.HeightMm, SlabThicknessMm = l.SlabThicknessMm, BeamDepthMm = l.BeamDepthMm });
        m.Project["name"] = s.Info.Name;
        m.Project["client_name"] = s.Info.ClientName;
        m.Project["company_name"] = s.Info.CompanyName;
        m.Project["location"] = s.Info.Location;
        m.Project["address"] = s.Info.Address;
        m.Sheets["masonry"] = s.MasonryWalls;
        m.Sheets["masonry_openings"] = s.MasonryOpenings;
        m.Sheets["doors"] = s.Doors;
        m.Sheets["windows"] = s.Windows;
        m.Sheets["columns"] = s.Columns;
        m.Sheets["lintels"] = s.Lintels;
        m.Sheets["flooring"] = s.Flooring;
        m.Sheets["slabs"] = s.Slabs;
        return m;
    }

    static void WriteBack(ProjectStore s, ImportModel m)
    {
        // levels: update existing, add new, drop ones the undo removed (never Lvl0)
        for (int i = s.Levels.Count - 1; i >= 1; i--)
            if (m.Levels.All(r => r.Id != s.Levels[i].Id)) s.Levels.RemoveAt(i);
        foreach (var r in m.Levels)
        {
            var l = s.Levels.FirstOrDefault(x => x.Id == r.Id);
            if (l is null)
                s.Levels.Add(new LevelDef { Id = r.Id, Name = r.Name, HeightMm = r.HeightMm, SlabThicknessMm = r.SlabThicknessMm, BeamDepthMm = r.BeamDepthMm });
            else if (r.Id != "Lvl0")
            {
                l.HeightMm = r.HeightMm; l.SlabThicknessMm = r.SlabThicknessMm; l.BeamDepthMm = r.BeamDepthMm;
            }
        }
        string P(string k) => m.Project.TryGetValue(k, out var v) ? v : "";
        s.Info.Name = P("name"); s.Info.ClientName = P("client_name"); s.Info.CompanyName = P("company_name");
        s.Info.Location = P("location"); s.Info.Address = P("address");
    }
}
