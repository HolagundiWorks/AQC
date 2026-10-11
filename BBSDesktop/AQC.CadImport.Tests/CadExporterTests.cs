using System.Text.Json.Nodes;
using Aqc.CadImport;
using Xunit;

namespace AQC.CadImport.Tests;

public class CadExporterTests
{
    static ImportModel Model()
    {
        var m = new ImportModel();
        m.Levels.Add(new LevelRec { Id = "Lvl0", Name = "Plinth", HeightMm = 3200, SlabThicknessMm = 150, BeamDepthMm = 300 });
        m.Levels.Add(new LevelRec { Id = "Lvl1", Name = "Ground", HeightMm = 3150, SlabThicknessMm = 150, BeamDepthMm = 450 });
        m.Levels.Add(new LevelRec { Id = "Lvl2", Name = "First", HeightMm = 3000, SlabThicknessMm = 125, BeamDepthMm = 450 });
        m.Project["name"] = "Residence";
        m.Project["client_name"] = "S. Rao";
        m.Project["company_name"] = "";
        m.Project["location"] = "Hospet";
        return m;
    }

    static JsonObject Export(ImportModel m) => (JsonObject)JsonNode.Parse(CadExporter.Build(m, new DateTime(2026, 10, 11, 9, 0, 0, DateTimeKind.Utc)))!;

    [Fact]
    public void Writes_v1_header_in_millimetres()
    {
        var j = Export(Model());
        Assert.Equal("hcw-aqc-bridge", (string)j["format"]!);
        Assert.Equal(1, (int)j["version"]!);
        Assert.Equal("mm", (string)j["units"]!);
        Assert.Equal("AQC", (string)j["source"]!["app"]!);
        Assert.Equal("2026-10-11T09:00:00Z", (string)j["source"]!["exported"]!);
        Assert.False(string.IsNullOrWhiteSpace((string)j["source"]!["drawing_id"]!));
    }

    [Fact]
    public void Plinth_is_never_exported_and_floors_keep_their_values()
    {
        var levels = (JsonArray)Export(Model())["levels"]!;
        Assert.Equal(new[] { "Ground", "First" }, levels.Select(l => (string)l!["name"]!));
        Assert.Equal(3150, (double)levels[0]!["height_mm"]!);
        Assert.Equal(125, (double)levels[1]!["slab_thickness_mm"]!);
        Assert.Equal(450, (double)levels[1]!["beam_depth_mm"]!);
    }

    [Fact]
    public void Beam_depths_are_distinct_and_sorted()
    {
        var d = ((JsonArray)Export(Model())["beam_depths_mm"]!).Select(x => (double)x!).ToArray();
        Assert.Equal(new[] { 300.0, 450.0 }, d);
    }

    [Fact]
    public void Empty_and_default_project_fields_are_left_out()
    {
        var m = Model();
        var p = (JsonObject)Export(m)["project"]!;
        Assert.False(p.ContainsKey("company_name"));
        Assert.Equal("S. Rao", (string)p["client_name"]!);

        m.Project["name"] = "Untitled Project";
        Assert.False(((JsonObject)Export(m)["project"]!).ContainsKey("name"));
    }

    [Fact]
    public void Never_sends_geometry_or_derived_sections()
    {
        var m = Model();
        m.Sheet("masonry").Add(new Dictionary<string, string> { ["mark"] = "MW-Lvl1-001", ["length"] = "5000" });
        var j = Export(m);
        foreach (var k in new[] { "walls", "openings", "columns", "lintels", "rooms", "slabs", "plaster", "painting" })
            Assert.False(j.ContainsKey(k), k);
    }

    [Fact]
    public void Reuses_the_drawing_id_of_the_last_import()
    {
        var m = Model();
        m.Sheet("masonry").Add(new Dictionary<string, string> { ["source"] = "hcwcad:0b7d9c34", ["source_ref"] = "W1" });
        Assert.Equal("0b7d9c34", (string)Export(m)["source"]!["drawing_id"]!);
    }

    [Fact]
    public void Export_reads_back_through_the_importer_unchanged()
    {
        var a = Model();
        var file = BridgeFile.Parse(CadExporter.Build(a));

        var b = new ImportModel();
        b.Levels.Add(new LevelRec { Id = "Lvl0", Name = "Plinth", HeightMm = 3200, SlabThicknessMm = 150, BeamDepthMm = 450 });
        CadImporter.Apply(CadImporter.Plan(file, b), b);

        Assert.Equal(new[] { "Ground", "First" }, b.Levels.Skip(1).Select(l => l.Name));
        Assert.Equal(a.Levels[2].SlabThicknessMm, b.Levels[2].SlabThicknessMm);
        Assert.Equal("Residence", b.Project["name"]);
        Assert.Equal(450, b.Levels[0].BeamDepthMm);   // the importing project's plinth is untouched
    }
}
