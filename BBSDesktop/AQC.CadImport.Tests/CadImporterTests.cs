using Aqc.CadImport;
using Xunit;

namespace AQC.CadImport.Tests;

public class CadImporterTests
{
    static string Fixture(string n) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", n));
    static BridgeFile House() => BridgeFile.Parse(Fixture("small-house.json"));

    static ImportModel NewModel(bool withFirstFloor = false)
    {
        var m = new ImportModel();
        m.Levels.Add(new LevelRec { Id = "Lvl0", Name = "Plinth", HeightMm = 3200, SlabThicknessMm = 150, BeamDepthMm = 450 });
        if (withFirstFloor)
            m.Levels.Add(new LevelRec { Id = "Lvl1", Name = "First floor", HeightMm = 3000, SlabThicknessMm = 150, BeamDepthMm = 450 });
        m.Project["name"] = "Untitled Project";
        return m;
    }

    static ImportPlan Run(BridgeFile f, ImportModel m, ImportOptions? o = null)
    {
        var p = CadImporter.Plan(f, m, o);
        CadImporter.Apply(p, m);
        return p;
    }

    [Fact]
    public void SmallHouse_into_empty_project_adds_expected_rows()
    {
        var m = NewModel();
        var p = Run(House(), m);

        Assert.Equal(3, m.Sheet("masonry").Count);
        Assert.Equal(5, m.Sheet("masonry_openings").Count);
        Assert.Equal(2, m.Sheet("doors").Count);
        Assert.Equal(2, m.Sheet("windows").Count);
        Assert.Equal(2, m.Sheet("columns").Count);
        Assert.Single(m.Sheet("lintels"));
        Assert.Equal(2, m.Sheet("flooring").Count);
        Assert.Single(m.Sheet("slabs"));

        // the 114.3 mm wall is flagged as AQC's 110 mm build
        Assert.Contains(p.Items, i => i.Section == "masonry" && i.Ref == "W0003" && i.Reason.Contains("110"));
        // every opening points at its wall's AQC mark
        Assert.All(m.Sheet("masonry_openings"), r => Assert.StartsWith("MW-Lvl1-", r["wall_mark"]));
        // schedule rows carry counts and never deduct a second time
        var d2 = m.Sheet("doors").Single(r => r["mark"] == "D2");
        Assert.Equal("2", d2["nos"]);
        Assert.All(m.Sheet("doors").Concat(m.Sheet("windows")), r => Assert.Equal("No", r["deduct_from_wall"]));
        // columns: no height written (AQC computes it)
        Assert.All(m.Sheet("columns"), r => Assert.False(r.ContainsKey("height")));
        Assert.Equal(new[] { "MW-Lvl1-001", "MW-Lvl1-002", "MW-Lvl1-003" }, m.Sheet("masonry").Select(r => r["mark"]));
    }

    [Fact]
    public void Importing_twice_adds_and_updates_nothing()
    {
        var m = NewModel();
        Run(House(), m);
        var again = CadImporter.Plan(House(), m);
        Assert.Equal(0, again.Count(PlanAction.Add));
        Assert.Equal(0, again.Count(PlanAction.Update));
        Assert.Equal(0, again.Count(PlanAction.Flagged));
    }

    [Fact]
    public void Levels_only_leaves_plinth_matches_by_name_and_adds_new()
    {
        var m = NewModel();
        m.Levels.Add(new LevelRec { Id = "Lvl1", Name = "Ground", HeightMm = 3000, SlabThicknessMm = 150, BeamDepthMm = 450 });
        var plinth = (m.Levels[0].HeightMm, m.Levels[0].SlabThicknessMm, m.Levels[0].BeamDepthMm);

        Run(BridgeFile.Parse(Fixture("levels-only.json")), m);

        Assert.Equal(plinth, (m.Levels[0].HeightMm, m.Levels[0].SlabThicknessMm, m.Levels[0].BeamDepthMm));
        Assert.Equal(3150, m.Levels[1].HeightMm);          // Ground matched by name and updated
        Assert.Equal("Lvl2", m.Levels[2].Id);              // First added at the end
        Assert.Equal("First", m.Levels[2].Name);
        Assert.Equal("Residence for Mr Rao", m.Project["name"]);
    }

    [Fact]
    public void Changed_wall_height_updates_one_row_without_duplicating()
    {
        var m = NewModel();
        Run(House(), m);
        var json = Fixture("small-house.json").Replace("\"ref\": \"W0001\", \"level\": \"Ground\", \"length_mm\": 9000, \"height_mm\": 3000",
            "\"ref\": \"W0001\", \"level\": \"Ground\", \"length_mm\": 9000, \"height_mm\": 3300");
        var p = Run(BridgeFile.Parse(json), m);

        Assert.Equal(1, p.Count(PlanAction.Update) - p.Of(PlanAction.Update).Count(i => i.Section == "level" || i.Section == "project"));
        Assert.Equal(3, m.Sheet("masonry").Count);
        Assert.Equal("3300", m.Sheet("masonry").Single(r => r["source_ref"] == "W0001")["height"]);
    }

    [Fact]
    public void Row_edited_in_AQC_is_flagged_and_kept()
    {
        var m = NewModel();
        Run(House(), m);
        var wall = m.Sheet("masonry").Single(r => r["source_ref"] == "W0001");
        wall["length"] = "9100";   // user edit
        var p = CadImporter.Plan(House(), m);
        Assert.Contains(p.Items, i => i.Action == PlanAction.Flagged && i.Ref == "W0001");
        CadImporter.Apply(p, m);
        Assert.Equal("9100", wall["length"]);

        var forced = CadImporter.Plan(House(), m, new ImportOptions { OverwriteEdited = true });
        CadImporter.Apply(forced, m);
        Assert.Equal("9000", wall["length"]);
    }

    [Fact]
    public void Removed_cad_items_are_reported_not_deleted()
    {
        var m = NewModel();
        Run(House(), m);
        var json = Fixture("small-house.json").Replace("\"ref\": \"W0003\"", "\"ref\": \"W0009\"");
        var p = CadImporter.Plan(BridgeFile.Parse(json), m);
        Assert.Contains(p.Items, i => i.Action == PlanAction.NoLongerInDrawing && i.Ref == "W0003");
        CadImporter.Apply(p, m);
        Assert.Contains(m.Sheet("masonry"), r => r["source_ref"] == "W0003");
    }

    [Fact]
    public void Rows_with_other_source_are_never_touched_and_do_not_collide_on_marks()
    {
        var m = NewModel();
        var mine = new Dictionary<string, string> { ["mark"] = "MW-Lvl1-001", ["level"] = "Lvl1", ["length"] = "1", ["source"] = "manual" };
        m.Sheet("masonry").Add(mine);
        Run(House(), m);
        Assert.Equal("1", mine["length"]);
        Assert.Equal(4, m.Sheet("masonry").Select(r => r["mark"]).Distinct().Count());
    }

    [Fact]
    public void Undo_reverses_the_whole_import()
    {
        var m = NewModel();
        var p = CadImporter.Plan(House(), m);
        var undo = CadImporter.Apply(p, m);
        undo();
        Assert.All(m.Sheets.Values, s => Assert.Empty(s));
        Assert.Single(m.Levels);
        Assert.Equal("Untitled Project", m.Project["name"]);
    }

    [Fact]
    public void Schedule_count_mismatch_is_reported()
    {
        var json = Fixture("small-house.json").Replace("\"nos\": 2, \"type\": \"single\" }", "\"nos\": 5, \"type\": \"single\" }");
        var p = CadImporter.Plan(BridgeFile.Parse(json), NewModel());
        Assert.Contains(p.Items, i => i.Section == "opening_schedule" && i.Reason.Contains("schedule says 5"));
    }

    [Fact]
    public void Other_drawing_is_detected()
    {
        var m = NewModel();
        Run(House(), m);
        var other = BridgeFile.Parse(Fixture("levels-only.json"));
        Assert.Equal("0b7d9c34-1e6f-4f0b-8a13-9b2c7d5e41aa", CadImporter.Plan(other, m).OtherDrawingId);
    }

    [Theory]
    [InlineData("\"version\": 1", "\"version\": 2")]
    [InlineData("\"units\": \"mm\"", "\"units\": \"m\"")]
    [InlineData("\"hcw-aqc-bridge\"", "\"other\"")]
    public void Bad_files_are_refused(string from, string to)
    {
        var json = Fixture("small-house.json").Replace(from, to);
        Assert.Throws<BridgeFormatException>(() => BridgeFile.Parse(json));
    }
}
